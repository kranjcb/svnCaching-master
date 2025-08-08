using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using Newtonsoft.Json;
using NLog;
using SharpSvn;
using i4;

namespace svnCaching
{
    public class Svn : IDisposable
    {
        private readonly SvnClient client = new SvnClient();
        private Uri uri;
        private readonly string localPath;
        private readonly static ILogger logger = LogManager.GetCurrentClassLogger();
        private readonly string jsonFilePath;
        private readonly TimeSpan maxAgeTrunk;
        private readonly TimeSpan maxAgeDays;
        private static readonly Mutex syncMutex = new Mutex(false, "Global\\SvnCachingSyncMutex");
        private readonly object clientLock = new object();
        private bool disposedValue = false;

        public Svn(Config configuration)
        {
            var url = configuration.RepositoryUrl;
            this.localPath = configuration.ExportDirectory;
            this.jsonFilePath = configuration.AccessTimesJson;
            this.maxAgeTrunk = TimeSpan.FromDays(configuration.MaxAgeDaysTrunk);
            this.maxAgeDays = TimeSpan.FromDays(configuration.MaxAgeDays);
            CreateClient(url, new NetworkCredential(configuration.Username, configuration.Password));
        }

        public static Config GetFromFile(string path)
        {
            string json = File.ReadAllText(path);
            return JsonConvert.DeserializeObject<Config>(json);
        }

        private void CreateClient(string url, ICredentials credentials)
        {
            client.Authentication.DefaultCredentials = credentials;
            client.Authentication.SslServerTrustHandlers += new EventHandler<SharpSvn.Security.SvnSslServerTrustEventArgs>(SVN_SSL_Override);

            uri = new Uri(url);
        }

        private static void SVN_SSL_Override(object sender, SharpSvn.Security.SvnSslServerTrustEventArgs e)
        {
            if (Environment.GetEnvironmentVariable("SVN_ALLOW_UNTRUSTED_SSL") == "1")
            {
                e.AcceptedFailures = e.Failures;
                e.Save = true;
            }
        }

        public void Update(string directory)
        {
            ThrowIfDisposed();
            string destination = CombinePath(localPath, directory);
            var target = new SvnUriTarget(new Uri(CombinePath(uri.ToString(), directory)));
            Dictionary<string, FileAccessInfo> accessTimes = null;
            bool hasHandle = false;
            try
            {
                try
                {
                    hasHandle = syncMutex.WaitOne();
                }
                catch (AbandonedMutexException ex)
                {
                    logger.Warn(ex, "Abandoned mutex detected.");
                    hasHandle = true;
                }
                lock (clientLock)
                {
                    accessTimes = GetFromJson();
                    if (!Directory.Exists(destination))
                    {
                        client.CheckOut(target, destination, out _);
                        logger.Trace("Checkingout {0} to {1}", target, destination);
                        accessTimes[destination] = new FileAccessInfo(destination, DateTime.Now);
                    }

                    else
                    {
                        if (accessTimes.TryGetValue(destination, out FileAccessInfo accesTime))
                        {

                            client.Update(destination, out _);
                        }
                        logger.Trace("Updating {0}", destination);
                        accesTime.LastAccessTime = DateTime.Now;
                        accessTimes[destination] = accesTime;

                    }
                }
            }
            catch (Exception ex)
            {
                if (Directory.Exists(destination))
                {
                    ForceDeleteDirectory(destination);
                    if (accessTimes != null)
                    {
                        accessTimes.Remove(destination);
                        LogAccessTimes(accessTimes);
                    }
                    logger.Trace(ex, "Deleting directory {0}", destination);
                }
                else
                {
                    ex.Data.Add("Destination", destination);
                    ex.Data.Add("Directory", directory);
                    ex.Throw();
                }
            }
            finally
            {
                LogAccessTimes(accessTimes);
                if (hasHandle)
                    syncMutex.ReleaseMutex();
            }

        }

        public void ExportToRevision(string directory, int revision)
        {
            ThrowIfDisposed();
            var target = new SvnUriTarget(new Uri(CombinePath(uri.ToString(), directory)), revision);
            string destination = CombinePath(localPath, directory, revision);
            Dictionary<string, FileAccessInfo> accessTimes = null;
            SvnExportArgs exportArgs = new SvnExportArgs
            {
                Overwrite = true
            };
            bool hasHandle = false;
            try
            {
                try
                {
                    hasHandle = syncMutex.WaitOne();
                }
                catch (AbandonedMutexException ex)
                {
                    logger.Warn(ex, "Abandoned mutex detected.");
                    hasHandle = true;
                }
                lock (clientLock)
                {
                    accessTimes = GetFromJson();
                    if (!Directory.Exists(destination))
                    {

                        client.Export(target, destination, exportArgs, out _);

                        logger.Trace("Exporting {0} to {1}", target, destination);
                        accessTimes[destination] = new FileAccessInfo(destination, DateTime.Now);
                    }
                }
            }
            catch (Exception ex)
            {
                ex.Data.Add("Destination", destination);
                ex.Data.Add("Revision", revision);
                ex.Data.Add("Directory", directory);
                ex.Throw();
            }
            finally
            {
                LogAccessTimes(accessTimes);
                if (hasHandle)
                    syncMutex.ReleaseMutex();
            }
        }

        private Dictionary<string, FileAccessInfo> GetFromJson()
        {

            if (!File.Exists(jsonFilePath))
                return new Dictionary<string, FileAccessInfo>();
            lock (clientLock)
            {
                try
                {
                    string json = File.ReadAllText(jsonFilePath);
                    var list = JsonConvert.DeserializeObject<List<FileAccessInfo>>(json) ?? new List<FileAccessInfo>();
                    return list.ToDictionary(f => f.FolderPath, f => f);
                }
                catch (Exception ex)
                {
                    ex.Data.Add("JsonFilePath", jsonFilePath);
                    ex.Throw();
                    return new Dictionary<string, FileAccessInfo>();
                }
            }
        }

        private void LogAccessTimes(Dictionary<string, FileAccessInfo> files)
        {
            if (files == null)
                return;
            lock (clientLock)
            {
                var list = files.Values.ToList();
                string json = JsonConvert.SerializeObject(list, Formatting.Indented);
                File.WriteAllText(jsonFilePath, json);
            }
        }

        private void CleanFolders(string path, TimeSpan maxAge, List<Exception> exceptions)
        {
            if (!Directory.Exists(path)) return;

            var now = DateTime.Now;
            var folders = Directory.GetDirectories(path, "*", SearchOption.TopDirectoryOnly);
            Dictionary<string, FileAccessInfo> accessTimes = null;
            bool hasHandle = false;
            try
            {
                try
                {
                    hasHandle = syncMutex.WaitOne();
                }
                catch (AbandonedMutexException ex)
                {
                    logger.Warn(ex, "Abandoned mutex detected.");
                    hasHandle = true;
                }

                accessTimes = GetFromJson();
                foreach (var folder in folders)
                {
                    try
                    {
                        if (accessTimes.TryGetValue(folder, out FileAccessInfo fileAccess) && (now - fileAccess.LastAccessTime) > maxAge)
                        {
                            ForceDeleteDirectory(folder);
                            logger.Trace("Clearing {0}", folder);
                            accessTimes.Remove(folder);
                        }
                    }
                    catch (Exception ex)
                    {
                        ex.Data.Add("Path", path);
                        ex.Data.Add("Folder", folder);
                        exceptions.Add(ex);
                    }
                }
            }
            finally
            {
                LogAccessTimes(accessTimes);
                if (hasHandle)
                    syncMutex.ReleaseMutex();
            }
        }

        public void Clean()
        {
            ThrowIfDisposed();
            bool hasHandle = false;
            try
            {
                try
                {
                    hasHandle = syncMutex.WaitOne();
                }
                catch (AbandonedMutexException ex)
                {
                    logger.Warn(ex, "Abandoned mutex detected.");
                    hasHandle = true;
                }

                var exceptions = new List<Exception>();
                CleanFolders(localPath, maxAgeTrunk, exceptions);
                CleanFolders(Path.Combine(localPath, "tags"), maxAgeDays, exceptions);
                CleanFolders(Path.Combine(localPath, "branches"), maxAgeDays, exceptions);
                if (exceptions.Count > 0)
                {
                    var ex = new AggregateException("Errors occurred during cleaning", exceptions);
                    ex.Throw();
                }
            }
            finally
            {
                if (hasHandle)
                {
                    syncMutex.ReleaseMutex();
                }
            }
        }

        private static void ForceDeleteDirectory(string path)
        {
            if (!Directory.Exists(path))
                return;

            foreach (string file in Directory.GetFiles(path, "*", SearchOption.AllDirectories))
            {
                var attributes = File.GetAttributes(file);
                if ((attributes & FileAttributes.ReadOnly) == FileAttributes.ReadOnly)
                {
                    File.SetAttributes(file, attributes & ~FileAttributes.ReadOnly);
                }
            }

            Directory.Delete(path, true);
        }

        public static string CombinePath(string localPath, string directory, int revision)
        {
            return Path.Combine(localPath, $"{directory}_{revision}");
        }

        public static string CombinePath(string localPath, string directory)
        {
            return Path.Combine(localPath, directory);
        }

        private void ThrowIfDisposed()
        {
            if (disposedValue)
                throw new ObjectDisposedException(nameof(Svn));
        }

        ~Svn()
        {
            Dispose(false);
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }
        protected virtual void Dispose(bool disposing)
        {
            if (disposedValue)
                return;

            if (disposing)
            {
                client?.Dispose();
            }

            disposedValue = true;
        }
    }
}