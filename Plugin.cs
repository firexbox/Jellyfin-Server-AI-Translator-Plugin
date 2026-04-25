using System.Reflection;
using Jellyfin.Plugin.AITranslator.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Plugins;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.AITranslator
{
    /// <summary>
    /// Main plugin class for Jellyfin AI Translator.
    /// Also implements IPluginServiceRegistrator to ensure controller discovery
    /// on platforms where standalone IPluginServiceRegistrator classes are not found.
    /// </summary>
    public class Plugin : MediaBrowser.Common.Plugins.IPlugin, IHasPluginConfiguration, IHasWebPages, IPluginServiceRegistrator
    {
        private static readonly Guid PluginId = Guid.Parse("3BE29809-6E1A-4D01-8B20-67A0B1A3C9F8");
        private PluginConfiguration? _configuration;

        public Plugin()
        {
            Instance = this;
            var assembly = typeof(Plugin).Assembly;
            var asmName = assembly.GetName();
            Version = asmName.Version ?? new Version(1, 0, 0);
            AssemblyFilePath = assembly.Location;
            DataFolderPath = Path.Combine(
                Path.GetDirectoryName(AssemblyFilePath) ?? ".",
                "AITranslator");
            Id = PluginId;
        }

        /// <summary>
        /// Called by Jellyfin's PluginManager via IPluginServiceRegistrator.
        /// Registers the plugin assembly as an MVC ApplicationPart so controllers
        /// are discoverable by ASP.NET Core routing on all platforms.
        /// </summary>
        public void RegisterServices(IServiceCollection services, IServerApplicationHost appHost)
        {
            var pluginAssembly = typeof(Plugin).Assembly;
            var pluginAssemblyName = pluginAssembly.GetName().Name ?? "Jellyfin.Plugin.AITranslator";

            try
            {
                // Strategy 1: If ApplicationPartManager already exists, add directly
                var descriptor = services.FirstOrDefault(d => d.ServiceType == typeof(ApplicationPartManager));
                if (descriptor?.ImplementationInstance is ApplicationPartManager existingPartManager)
                {
                    if (!existingPartManager.ApplicationParts.Any(p => p.Name == pluginAssemblyName))
                    {
                        existingPartManager.ApplicationParts.Add(new AssemblyPart(pluginAssembly));
                    }
                }
                else
                {
                    // Strategy 2: Create new ApplicationPartManager before Jellyfin's AddMvc uses it
                    var partManager = new ApplicationPartManager();
                    partManager.ApplicationParts.Add(new AssemblyPart(pluginAssembly));
                    services.AddSingleton(partManager);
                }
            }
            catch
            {
                // Ignore - ApplicationPartManager may not be available on all builds
            }
        }

        public static Plugin? Instance { get; private set; }
        public string Name => "AI Translator";
        public string Description => "AI-powered subtitle translation using DeepSeek, OpenAI, or Ollama.";
        public Guid Id { get; }
        public Version Version { get; }
        public string AssemblyFilePath { get; }
        public string DataFolderPath { get; }
        public bool CanUninstall => true;
        public Type ConfigurationType => typeof(PluginConfiguration);

        BasePluginConfiguration IHasPluginConfiguration.Configuration => Configuration;

        public PluginConfiguration Configuration
        {
            get
            {
                _configuration ??= LoadConfig();
                return _configuration;
            }
        }

        public PluginInfo GetPluginInfo()
            => new PluginInfo(Name, Version, Description, Id, CanUninstall);

        public void OnUninstalling() { }

        public IEnumerable<PluginPageInfo> GetPages()
        {
            return new[]
            {
                new PluginPageInfo
                {
                    Name = Name,
                    EmbeddedResourcePath = $"{typeof(Plugin).Namespace}.Configuration.configPage.html"
                },
                new PluginPageInfo
                {
                    Name = "aitranslator",
                    EmbeddedResourcePath = $"{typeof(Plugin).Namespace}.Configuration.configPage.html"
                }
            };
        }

        public void UpdateConfiguration(BasePluginConfiguration config)
        {
            if (config is PluginConfiguration pluginConfig)
            {
                _configuration = pluginConfig;
                SaveConfig(pluginConfig);
            }
        }

        public void SaveConfig(PluginConfiguration config)
        {
            _configuration = config;
            var dir = Path.GetDirectoryName(ConfigPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(ConfigPath,
                System.Text.Json.JsonSerializer.Serialize(config,
                    new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        }

        private string ConfigPath => Path.Combine(
            Path.GetDirectoryName(AssemblyFilePath) ?? ".",
            "AITranslator_config.json");

        private PluginConfiguration LoadConfig()
        {
            try
            {
                if (File.Exists(ConfigPath))
                {
                    var json = File.ReadAllText(ConfigPath);
                    var cfg = System.Text.Json.JsonSerializer.Deserialize<PluginConfiguration>(json);
                    if (cfg != null) return cfg;
                }
            }
            catch { }
            var def = new PluginConfiguration();
            SaveConfig(def);
            return def;
        }
    }
}
