using Lykke.SettingsReader.Attributes;
namespace Lykke.Service.Dynamic.Api.Core.Settings.ServiceSettings

{
    public class DynamicApiSettings
    {
        //mark schroeder 20170731 added optional attribute "In this case if your json string is not contain the field, exception won't be threw."
        //[Optional]
        public DbSettings Db { get; set; }
        //[Optional]
        public string Network { get; set; }
        //[Optional]
        public string InsightApiUrl { get; set; }
        //[Optional]
        public decimal Fee { get; set; }
        //[Optional]
        public int MinConfirmations { get; set; }
    }
}
