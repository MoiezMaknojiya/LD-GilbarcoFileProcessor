using Newtonsoft.Json;
using System.Xml;
using System.Xml.Linq;

namespace ApiLibrary.Utilities
{
    public class XmlJsonConverter
    {
        public string ConvertXmlToJson(XDocument xDoc, Action<string> logWarning, Action<string> logError, Action<string> logInfo)
        {
            try
            {
                if (xDoc == null)
                {
                    logWarning("XDocument is null.\n");
                    return "";
                }

                string json = JsonConvert.SerializeXNode(
                     xDoc,
                     Newtonsoft.Json.Formatting.Indented,
                     omitRootObject: false
                );

                long jsonSizeBytes = System.Text.Encoding.UTF8.GetByteCount(json);
                double jsonSizeKB = jsonSizeBytes / 1024.0;
                double jsonSizeMB = jsonSizeKB / 1024.0;

                if (jsonSizeMB >= 1)
                {
                    logInfo($"XML converted to JSON successfully. Size: {jsonSizeKB} KB ({jsonSizeMB:F2} MB) ({jsonSizeBytes} bytes).\n");
                }
                else
                {
                    logInfo($"XML converted to JSON successfully. Size: {jsonSizeKB:F2} KB ({jsonSizeBytes} bytes).\n");
                }

                return json;
            }
            catch (Exception ex)
            {
                logError($"Error converting XML to JSON: {ex.Message}.\n\n");
                return "";
            }
        }
    }
}
