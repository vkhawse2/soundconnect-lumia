using System;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;
using SoundConnect.Sony;

namespace SoundConnect
{
    /// <summary>
    /// The Start-screen live tile, Windows Phone style: while the XM4 is
    /// connected it shows the battery %, the noise-control mode and the EQ
    /// preset; when the headphones disconnect it reverts to the plain
    /// Sound Connect tile from the manifest.
    /// </summary>
    public static class LiveTile
    {
        static string Esc(string s)
        {
            return (s ?? "").Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
        }

        public static string DescribeNc(NoiseControlState nc)
        {
            if (nc == null) return "";
            switch (nc.Mode)
            {
                case NoiseMode.NoiseCancelling: return "Noise Cancelling";
                case NoiseMode.Ambient: return "Ambient Sound · level " + nc.AmbientLevel;
                default: return "Noise control off";
            }
        }

        public static void Update(int batteryPct, bool charging, string ncText, string eqText)
        {
            string batt = batteryPct + "%";
            string charge = charging ? " · charging" : "";
            string xml =
                "<tile><visual version=\"3\">" +
                "<binding template=\"TileSmall\" branding=\"none\">" +
                    "<text hint-style=\"body\" hint-align=\"center\">" + batt + "</text>" +
                "</binding>" +
                "<binding template=\"TileMedium\" branding=\"name\">" +
                    "<text hint-style=\"title\">" + batt + "</text>" +
                    "<text hint-style=\"caption\">" + Esc(ncText) + "</text>" +
                "</binding>" +
                "<binding template=\"TileWide\" branding=\"name\">" +
                    "<group>" +
                        "<subgroup hint-weight=\"22\"><image src=\"ms-appx:///Assets/Square150x150Logo.png\"/></subgroup>" +
                        "<subgroup>" +
                            "<text hint-style=\"subtitle\">WH-1000XM4 - " + batt + Esc(charge) + "</text>" +
                            "<text hint-style=\"body\">" + Esc(ncText) + "</text>" +
                            "<text hint-style=\"caption\">" + Esc(eqText) + "</text>" +
                        "</subgroup>" +
                    "</group>" +
                "</binding>" +
                "<binding template=\"TileLarge\" branding=\"name\">" +
                    "<text hint-style=\"title\">" + batt + "</text>" +
                    "<text hint-style=\"body\">WH-1000XM4" + Esc(charge) + "</text>" +
                    "<text hint-style=\"caption\">" + Esc(ncText) + "</text>" +
                    "<text hint-style=\"caption\">" + Esc(eqText) + "</text>" +
                "</binding>" +
                "</visual></tile>";
            try
            {
                var doc = new XmlDocument();
                doc.LoadXml(xml);
                TileUpdateManager.CreateTileUpdaterForApplication().Update(new TileNotification(doc));
            }
            catch { /* a failed tile update must never break the app */ }
        }

        public static void Clear()
        {
            try { TileUpdateManager.CreateTileUpdaterForApplication().Clear(); }
            catch { }
        }
    }
}
