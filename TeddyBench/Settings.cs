using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TeddyBench
{
    public class Settings
    {
        public string Username = "";
        public bool NfcEnabled = false;
        public string NfcType = "Proxmark3";
        public bool DebugWindow = false;
        public bool DownloadJson = true;

        public static Settings FromFile(string file)
        {
            Settings s = null;

            try
            {
                s = JsonConvert.DeserializeObject<Settings>(File.ReadAllText(file));
            }
            catch (FileNotFoundException ex)
            {
                /* first start - defaults apply */
            }
            catch (Exception ex)
            {
                LogWindow.Log(LogWindow.eLogLevel.Error, "Failed to load settings from '" + file + "': " + ex.Message);
            }

            if(s != null)
            {
                return s;
            }

            return new Settings();
        }

        public bool Save(string file)
        {
            try
            {
                File.WriteAllText(file, JsonConvert.SerializeObject(this, Formatting.Indented));
                return true;
            }
            catch (Exception e)
            {
                LogWindow.Log(LogWindow.eLogLevel.Error, "Failed to save settings to '" + file + "': " + e.Message);
            }
            return false;
        }
    }
}
