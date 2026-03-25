using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;

namespace Autoclicker
{
    public class ConfigManager
    {
        private readonly string _configPath;
        private const string CONFIG_FILE = "Autoclicker.config";

        public ConfigManager()
        {
            _configPath = Path.Combine(AppContext.BaseDirectory, CONFIG_FILE);
        }

        public async Task SaveConfigAsync(AutoclickerConfig config)
        {
            try
            {
                var json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
                await File.WriteAllTextAsync(_configPath, json);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to save config: {ex.Message}");
            }
        }

        public async Task<AutoclickerConfig> LoadConfigAsync()
        {
            try
            {
                if (!File.Exists(_configPath))
                {
                    return GetDefaultConfig();
                }

                var json = await File.ReadAllTextAsync(_configPath);
                var config = JsonSerializer.Deserialize<AutoclickerConfig>(json);
                return config ?? GetDefaultConfig();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to load config: {ex.Message}");
                return GetDefaultConfig();
            }
        }

        private AutoclickerConfig GetDefaultConfig()
        {
            return new AutoclickerConfig
            {
                CPS = 10,
                ClickLocation = 0,
                CoordinateX = 0,
                CoordinateY = 0,
                ClickType = 0,
                ShowOnTop = false,
                BoundHotkey = null
            };
        }
    }

    public class AutoclickerConfig
    {
        public int? CPS { get; set; }
        public int? ClickLocation { get; set; }
        public int? CoordinateX { get; set; }
        public int? CoordinateY { get; set; }
        public int? ClickType { get; set; }
        public bool ShowOnTop { get; set; }
        public string? BoundHotkey { get; set; }
    }
}
