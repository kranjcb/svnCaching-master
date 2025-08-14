using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using SharpSvn;
using svnCaching;

namespace SvnTests
{
    [TestClass]
    public class SvnUnitTests
    {
        private Svn s;
        private string tempRepoPath;
        private string tempRepoUrl;
        private string jsonPath;
        private string localPath;

        [TestInitialize]
        public void Setup()
        {
            tempRepoPath = Path.Combine(Path.GetTempPath(), "TestRepo");
            RunSvnCommand("svnadmin", $"create \"{tempRepoPath}\"");
            localPath = Path.Combine(Path.GetTempPath(), "export");
            jsonPath = Path.Combine(localPath, "accessTimes.json");

            string projectPath = Path.Combine(Path.GetTempPath(), "ProjectToImport");
            Directory.CreateDirectory(Path.Combine(projectPath, "trunk"));
            Directory.CreateDirectory(Path.Combine(projectPath, "tags"));
            File.WriteAllText(Path.Combine(projectPath, "trunk", "hello.txt"), "hello world");

            RunSvnCommand("svn", $"import \"{projectPath}\" file:///{tempRepoPath.Replace("\\", "/")} -m \"Initial import\"");
            Directory.Delete(projectPath, true);
            tempRepoUrl = $"file:///{tempRepoPath.Replace("\\", "/")}";

            var config = new Config
            {
                ExportDirectory = localPath,
                MaxAgeDaysTrunk = 30,
                MaxAgeDays = 7,
                AccessTimesJson = jsonPath,
                RepositoryUrl = tempRepoUrl,
                Username = "",
                Password = ""
            };

            s = new Svn(config);
        }

        [TestCleanup]
        public void Cleanup()
        {
            if (Directory.Exists(localPath))
                ForceDeleteDirectory(localPath);
            if (Directory.Exists(tempRepoPath))
                ForceDeleteDirectory(tempRepoPath);
        }

        private void RunSvnCommand(string tool, string arguments)
        {
            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = tool,
                    Arguments = arguments,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            process.Start();
            process.WaitForExit();

            if (process.ExitCode != 0)
            {
                throw new Exception($"Failed to run {tool}: {arguments}\n{process.StandardError.ReadToEnd()}");
            }
        }

        private static void ForceDeleteDirectory(string path)
        {
            foreach (string file in Directory.GetFiles(path, "*", SearchOption.AllDirectories))
            {
                try
                {
                    var attrs = File.GetAttributes(file);
                    if ((attrs & FileAttributes.ReadOnly) == FileAttributes.ReadOnly)
                        File.SetAttributes(file, attrs & ~FileAttributes.ReadOnly);

                    File.Delete(file);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"⚠️ Could not delete file: {file}. Error: {ex.Message}");
                }
            }

            foreach (string dir in Directory.GetDirectories(path, "*", SearchOption.AllDirectories).Reverse())
            {
                try
                {
                    Directory.Delete(dir, true);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"⚠️ Could not delete directory: {dir}. Error: {ex.Message}");
                }
            }

            try
            {
                Directory.Delete(path, true);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"⚠️ Final delete failed: {path}. Error: {ex.Message}");
            }
        }

        [TestMethod]
        public void GetFromFile_WithInvalidPath_ThrowsFileNotFoundException()
        {
            Assert.ThrowsExactly<FileNotFoundException>(() =>
                Svn.GetFromFile("nonexistent.json"));
        }

        [TestMethod]
        public void UpdateTest()
        {
            s.Update("trunk");
            File.Delete(jsonPath);
            s.Update("trunk");
            s.Update("trunk");
            var path = Path.Combine(localPath, "trunk").Replace("/", "\\");
            Assert.IsTrue(Directory.Exists(path), "Directory was not created.");
        }

        [TestMethod]
        public void Clean_ShouldRemoveOldFoldersTrunk()
        {
            var folder = Path.Combine(localPath, "trunk-test");
            Directory.CreateDirectory(folder);

            var oldAccess = new List<FileAccessInfo>
            {
                new FileAccessInfo(folder, DateTime.Now.AddDays(-100))
            };

            File.WriteAllText(jsonPath, JsonConvert.SerializeObject(oldAccess, Formatting.Indented));

            s.Clean();

            Assert.IsFalse(Directory.Exists(folder), "Trunk dir should have been deleted.");
        }

        [TestMethod]
        public void Clean_ShouldRemoveFoldersWithNoAccess()
        {
            var folder = Path.Combine(localPath, "trunk-test");
            Directory.CreateDirectory(folder);
            s.Clean();

            Assert.IsFalse(Directory.Exists(folder), "Trunk dir should have been deleted.");
        }

        [TestMethod]
        public void Clean_ShouldRemoveOldFoldersTags_WithCheckout()
        {
            RunSvnCommand("svn", $"copy \"{tempRepoUrl}/trunk\" \"{tempRepoUrl}/tags/a\" -m \"Create tag a\"");
            s.Update(Path.Combine("tags", "a"));

            var folder = Path.Combine(localPath, "tags", "a");
            Assert.IsTrue(Directory.Exists(folder), "Tag 'a' directory was not created.");
            var oldAccess = new List<FileAccessInfo>
            {
                new FileAccessInfo(folder, DateTime.Now.AddDays(-100))
            };
            File.WriteAllText(jsonPath, JsonConvert.SerializeObject(oldAccess, Formatting.Indented));

            s.Clean();

            Assert.IsFalse(Directory.Exists(folder), "tags/a directory should have been deleted due to old access.");
        }

        [TestMethod]
        public void Clean_ShouldRemoveOldFoldersBranches()
        {
            var folder = Path.Combine(localPath, "branches", "b");
            Directory.CreateDirectory(folder);

            var oldAccess = new List<FileAccessInfo>
            {
                new FileAccessInfo(folder, DateTime.Now.AddDays(-100))
            };

            File.WriteAllText(jsonPath, JsonConvert.SerializeObject(oldAccess, Formatting.Indented));

            s.Clean();

            Assert.IsFalse(Directory.Exists(folder), "Branches dir should have been deleted.");
        }

        [TestMethod]
        public void Clean_Parallel()
        {
            for (int i = 0; i < 5; i++)
            {
                string folder = Path.Combine(localPath, $"trunk-{i}");
                Directory.CreateDirectory(folder);
            }

            var access = Directory.GetDirectories(localPath)
                .Select(dir => new FileAccessInfo(dir, DateTime.Now.AddDays(-100)))
                .ToList();

            File.WriteAllText(jsonPath, JsonConvert.SerializeObject(access, Formatting.Indented));

            Parallel.For(0, 5, _ => s.Clean());

            var remaining = Directory.GetDirectories(localPath);
            Assert.AreEqual(0, remaining.Length, "Some folders were not deleted.");
        }

        [TestMethod]
        public void ConcurrentUpdateSafety()
        {
            var config = new Config
            {
                ExportDirectory = localPath,
                MaxAgeDaysTrunk = 30,
                MaxAgeDays = 7,
                AccessTimesJson = jsonPath,
                RepositoryUrl = tempRepoUrl,
                Username = "",
                Password = ""
            };

            var s1 = new Svn(config);
            var s2 = new Svn(config);

            Parallel.Invoke(
                () => s1.Update("trunk"),
                () => s2.Update("trunk")
            );
            var folder = Path.Combine(localPath, "trunk");
            Assert.IsTrue(Directory.Exists(folder), "Directory was not created by concurrent updates.");
            s1.Dispose();
            s2.Dispose();
        }

        
        [TestMethod]
        public void Dispose_CanBeCalledMultipleTimes_NoException()
        {
            var config = new Config
            {
                ExportDirectory = localPath,
                MaxAgeDaysTrunk = 30,
                MaxAgeDays = 7,
                AccessTimesJson = jsonPath,
                RepositoryUrl = tempRepoUrl,
                Username = "",
                Password = ""
            };

            var svn = new Svn(config);
            svn.Dispose();

            svn.Dispose();
            var disposedField = typeof(Svn).GetField("disposedValue", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            Assert.IsNotNull(disposedField, "disposedValue field not found");

            var isDisposed = (bool)disposedField.GetValue(svn);
            Assert.IsTrue(isDisposed, "Svn should be marked as disposed after Dispose() is called.");
        }

        [TestMethod]
        public void UpdateMissingDirectory()
        {
            string path = Path.Combine(localPath, "tags", "test");
            Directory.CreateDirectory(path);
            Assert.ThrowsExactly<SvnInvalidNodeKindException>(() =>
            {
                s.Update(Path.Combine("tags", "test"));
            });
        }

        [TestMethod]
        public void SVN_SSL_Override_AcceptsFailures_WhenEnvironmentVariableSet()
        {
            try
            {
                var methodInfo = typeof(Svn).GetMethod("SVN_SSL_Override",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
                Environment.SetEnvironmentVariable("SVN_ALLOW_UNTRUSTED_SSL", "1");
                var eventArgs = new SharpSvn.Security.SvnSslServerTrustEventArgs(
                    SharpSvn.Security.SvnCertificateTrustFailures.UnknownCertificateAuthority,
                    "test.example.com",
                    "IssuerName",
                    "2023-01-01",
                    "2024-01-01",
                    "Fingerprint",
                    "CommonName",
                    "Realm",
                    true
                );
                methodInfo.Invoke(null, new object[] { null, eventArgs });

                Assert.IsTrue(eventArgs.Save, "Save should be true when environment variable is set");
                Assert.AreNotEqual(SharpSvn.Security.SvnCertificateTrustFailures.None, eventArgs.AcceptedFailures,
                    "Should accept failures when environment variable is set");
            }
            finally
            {
                Environment.SetEnvironmentVariable("SVN_ALLOW_UNTRUSTED_SSL", null);
            }
        }

        [TestMethod]
        public void GetFromJson_WithInvalidJsonContent_ThrowsException()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(jsonPath));
            File.WriteAllText(jsonPath, "{ This is not valid JSON }");
            var getFromJsonMethod = typeof(Svn).GetMethod("GetFromJson",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

            Assert.IsNotNull(getFromJsonMethod, "GetFromJson method not found via reflection");

            try
            {
                getFromJsonMethod.Invoke(s, null);
                Assert.Fail("Expected exception was not thrown");
            }
            catch (System.Reflection.TargetInvocationException ex)
            when (ex.InnerException is Newtonsoft.Json.JsonException)
            {
                Assert.IsTrue(ex.InnerException.Data.Contains("JsonFilePath"),
                    "Exception should contain JsonFilePath in Data");
                Assert.AreEqual(jsonPath, ex.InnerException.Data["JsonFilePath"],
                    "Exception should have correct JsonFilePath value");
            }
        }

        [TestMethod]
        public void Failed_Update()
        {
            Assert.ThrowsExactly<DirectoryNotFoundException>(() =>
            {
                s.Update("tronk");
            });
        }

        [TestMethod]
        public void ExportToRevisionTest()
        {
            Assert.ThrowsExactly<DirectoryNotFoundException>(() =>
            {
                s.ExportToRevision("trunk", 1900000);
            });
        }

        [TestMethod]
        public void ExportToRevision()
        {
            s.ExportToRevision("trunk", 1);
            File.Delete(jsonPath);
            s.ExportToRevision("trunk", 1);
            s.ExportToRevision("trunk", 1);
            Assert.IsTrue(Directory.Exists(localPath), "Directories were created.");
        }

        [TestMethod]
        public void ForceDeleteDirectory_DeletesReadOnlyFiles()
        {
            string testDir = Path.Combine(Path.GetTempPath(), "TestForceDelete");
            Directory.CreateDirectory(testDir);
            
            string nestedDir = Path.Combine(testDir, "NestedDir");
            Directory.CreateDirectory(nestedDir);
            string rootFile = Path.Combine(testDir, "root.txt");
            string nestedFile = Path.Combine(nestedDir, "nested.txt");
            
            File.WriteAllText(rootFile, "test content");
            File.WriteAllText(nestedFile, "nested content");
            
            File.SetAttributes(rootFile, FileAttributes.ReadOnly);
            File.SetAttributes(nestedFile, FileAttributes.ReadOnly);
            File.SetAttributes(nestedDir, FileAttributes.ReadOnly);
            
            Assert.IsTrue((File.GetAttributes(rootFile) & FileAttributes.ReadOnly) == FileAttributes.ReadOnly);
            Assert.IsTrue((File.GetAttributes(nestedFile) & FileAttributes.ReadOnly) == FileAttributes.ReadOnly);
            
            var forceDeleteMethod = typeof(Svn).GetMethod("ForceDeleteDirectory", 
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            
            Assert.IsNotNull(forceDeleteMethod, "ForceDeleteDirectory method not found");
            
            forceDeleteMethod.Invoke(null, new object[] { testDir });
           
            Assert.IsFalse(Directory.Exists(testDir), "Directory should be deleted");
        }
    }
}
