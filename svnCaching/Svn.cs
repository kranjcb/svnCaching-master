using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.CompilerServices;
using System.Threading;
using i4;
using Newtonsoft.Json;
using NLog;
using SharpSvn;

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
            e.AcceptedFailures = e.Failures;
            e.Save = true;
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

                accessTimes = GetFromJson();
                UpdateOrCheckoutRepository(target, destination, accessTimes);
            }
            catch (Exception ex)
            {
                if (Directory.Exists(destination))
                {
                    try
                    {
                        ForceDeleteDirectory(destination);
                        if(accessTimes != null) { 
                            accessTimes.Remove(destination);
                            LogAccessTimes(accessTimes);
                            logger.Trace(ex, "Deleting directory {0}", destination);
                        }
                    }
                    catch (Exception e)
                    {
                        e.Data.Add("Destination", destination);
                        e.Throw();
                    }
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

        private void UpdateOrCheckoutRepository(SvnUriTarget target, string destination, Dictionary<string, FileAccessInfo> accessTimes)
        {
            if (!Directory.Exists(destination))
            {
                client.CheckOut(target, destination, out _);
                logger.Trace("Checkingout {0} to {1}", target, destination);
                accessTimes[destination] = new FileAccessInfo(destination, DateTime.Now);
            }
            else
            {
                if (accessTimes.TryGetValue(destination, out FileAccessInfo fileAccess))
                {
                    client.Update(destination, out _);
                    logger.Trace("Updating {0}", destination);
                    fileAccess.LastAccessTime = DateTime.Now;
                    accessTimes[destination] = fileAccess;
                }
                else
                {
                    client.Update(destination, out _);
                    logger.Trace("Updating and adding access time {0}", destination);
                    accessTimes[destination] = new FileAccessInfo(destination, DateTime.Now);
                }
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
                accessTimes = GetFromJson();
                if (!Directory.Exists(destination))
                {
                    client.Export(target, destination, exportArgs, out _);
                    logger.Trace("Exporting {0} to {1}", target, destination);
                    accessTimes[destination] = new FileAccessInfo(destination, DateTime.Now);
                }
                else
                {
                    if (accessTimes.TryGetValue(destination, out FileAccessInfo fileAccess))
                    {
                        logger.Trace("Updating access time {0}", destination);
                        fileAccess.LastAccessTime = DateTime.Now;
                        accessTimes[destination] = fileAccess;
                    }
                    else
                    {
                        logger.Trace("Adding access time {0}", destination);
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
            }
            return new Dictionary<string, FileAccessInfo>();
        }

        private void LogAccessTimes(Dictionary<string, FileAccessInfo> files)
        {
            if (files == null)
                return;
            try
            {
                var list = files.Values.ToList();
                string json = JsonConvert.SerializeObject(list, Formatting.Indented);
                File.WriteAllText(jsonFilePath, json);
            }
            catch (Exception ex)
            {
                ex.Data.Add("jsonFilePath", jsonFilePath); 
                ex.Throw();
            }
        }
        private void CleanFolders(string path, TimeSpan maxAge, List<Exception> exceptions, Dictionary<string, FileAccessInfo> accessTimes)
        {
            if (!Directory.Exists(path)) return;

            var folders = Directory.GetDirectories(path, "*", SearchOption.TopDirectoryOnly);
            foreach (var folder in folders)
            {
                try
                {
                    if (accessTimes.TryGetValue(folder, out FileAccessInfo fileAccess) && !(folder.Equals(Path.Combine(localPath, "branches")) || folder.Equals(Path.Combine(localPath, "tags"))))
                    {
                        ProcessOldDirectories(fileAccess, maxAge, folder, exceptions); 
                    }
                    else if(!(folder.Equals(Path.Combine(localPath, "branches")) || folder.Equals(Path.Combine(localPath, "tags"))))
                    {
                        try
                        {
                            ForceDeleteDirectory(folder);
                            logger.Trace("Deleting directory {0} because it was not found in access times", folder);
                        }
                        catch (Exception ex)
                        {
                            exceptions.Add(ex);
                        }
                    }
                }
                catch (Exception ex)
                {
                    ex.Data.Add("Path", path);
                    ex.Data.Add("Folder", folder);
                    exceptions.Add(ex);
                }
            }
            RemoveExcessAccess(accessTimes);
        }

        private static void ProcessOldDirectories(FileAccessInfo fileAccess, TimeSpan maxAge, string folder, List<Exception> exceptions)
        {
            var now = DateTime.Now;
            if ((now - fileAccess.LastAccessTime) > maxAge)
            {
                try
                {
                    ForceDeleteDirectory(folder);
                    logger.Trace("Deleting old directory {0}", folder);
                }
                catch (Exception ex)
                {
                    exceptions.Add(ex);
                }
            }
        }

        private void RemoveExcessAccess(Dictionary<string, FileAccessInfo> accessTimes)
        {
            foreach (var folderToRemove in accessTimes.Keys.Where(k => !Directory.Exists(k)).ToList())
            {
                accessTimes.Remove(folderToRemove);
                logger.Trace("Removing access time for folder {0}", folderToRemove);
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
                Dictionary<string, FileAccessInfo> accessTimes = null;
                accessTimes = GetFromJson();
                string tagsPath = Path.Combine(localPath, "tags");
                string branchesPath = Path.Combine(localPath, "branches");
                var exceptions = new List<Exception>();
                CleanFolders(localPath, maxAgeTrunk, exceptions, accessTimes);
                CleanFolders(tagsPath, maxAgeDays, exceptions, accessTimes);
                CleanFolders(branchesPath, maxAgeDays, exceptions, accessTimes);
                LogAccessTimes(accessTimes);
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
