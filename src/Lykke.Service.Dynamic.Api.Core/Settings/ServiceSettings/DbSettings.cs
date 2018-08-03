using Lykke.SettingsReader.Attributes;
namespace Lykke.Service.Dynamic.Api.Core.Settings.ServiceSettings
{
    public class DbSettings
    {
        //mark schroeder 20170731 added optional attribute "In this case if your json string is not contain the field, exception won't be threw."
        //[Optional]
        public string LogsConnString { get; set; }
        //[Optional]
        public string DataConnString { get; set; }
    }
}
