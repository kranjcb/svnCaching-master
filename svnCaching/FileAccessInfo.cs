using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace svnCaching
{
    public class FileAccessInfo
    {
        public string FolderPath { get; set; }
        public DateTime LastAccessTime { get; set; }

        public FileAccessInfo(string folderPath, DateTime lastAccessTime)
        {
            this.FolderPath = folderPath;
            this.LastAccessTime = lastAccessTime;
        }

        public FileAccessInfo() { }

    }


}
