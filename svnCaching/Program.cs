using System;
using System.Diagnostics;
using System.IO;

namespace svnCaching
{
    public class Program
    {
        public const string Trunk = "trunk";
        public const string Branches = "branches";
        public const string Tags = "tags";
        private Program(){}
        public static void Main(string[] args)
        {
            Svn s = new Svn(Svn.GetFromFile("config.json"));
            var sw = Stopwatch.StartNew();
            s.Update(Path.Combine(Tags, "DBS"));
            s.Update(Path.Combine(Tags, "2. Semester"));
            s.Update(Path.Combine(Branches, "b"));
            s.Update(Path.Combine(Branches, "a0"));
            s.ExportToRevision(Trunk, 100);
            s.ExportToRevision(Trunk, 101);
            s.Clean();
            sw.Stop();

            Console.WriteLine($"All updates completed in {sw.Elapsed.TotalSeconds:F2} seconds");
        }
    }
}
