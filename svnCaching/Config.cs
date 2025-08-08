namespace svnCaching
{
    public class Config
    {
        public string ExportDirectory { get; set; }
        public int MaxAgeDaysTrunk { get; set; }
        public int MaxAgeDays { get; set; }
        public string AccessTimesJson { get; set; }
        public string RepositoryUrl { get; set; }
        public string Username { get; set; }
        public string Password { get; set; }
    }
}
