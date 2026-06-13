using System.Reflection;
using Jellyfin.Plugin.AITranslator.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.AITranslator
{
    /// <summary>
    /// Main plugin class for Jellyfin AI Translator.
    /// Also serves as a proxy registration point.
    /// </summary>
    public class Plugin : MediaBrowser.Common.Plugins.IPlugin, IHasPluginConfiguration, IHasWebPages
    {
        private static readonly Guid PluginId = Guid.Parse("3BE29809-6E1A-4D01-8B20-67A0B1A3C9F8");
        private PluginConfiguration? _configuration;
        private static bool _servicesRegistered;

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

            // Register MVC filter on first creation - disabled for now
            // TryRegisterFilter();
        }

        private static void TryRegisterFilter()
        {
            if (_servicesRegistered) return;
            try
            {
                // Try to access the global MVC options through ServiceProvider
                // This works when Jellyfin's DI has been fully initialized
                var serviceProvider = GetServiceProvider();
                if (serviceProvider == null) return;

                var mvcOptions = serviceProvider.GetService(
                    typeof(Microsoft.AspNetCore.Mvc.MvcOptions));
                if (mvcOptions is Microsoft.AspNetCore.Mvc.MvcOptions options)
                {
                    var loggerFactory = serviceProvider.GetService(
                        typeof(Microsoft.Extensions.Logging.ILoggerFactory)) 
                        as Microsoft.Extensions.Logging.ILoggerFactory;
                    
                    if (loggerFactory != null)
                    {
                        var logger = loggerFactory.CreateLogger("AI Translator");
                        options.Filters.Add(new Api.SubtitleTranslationFilter(logger));
                        Microsoft.Extensions.Logging.LoggerExtensions.LogInformation(logger, "AI Translator: Subtitle translation filter registered globally");
                    }
                }
                _servicesRegistered = true;
            }
            catch (Exception ex)
            {
                System.Console.Error.WriteLine($"AI Translator: Failed to register filter: {ex.Message}");
            }
        }

        private static IServiceProvider? GetServiceProvider()
        {
            try
            {
                // Find the IHost instance from Jellyfin's entry assembly
                var entryAsm = System.Reflection.Assembly.GetEntryAssembly();
                if (entryAsm == null) return null;

                var programType = entryAsm.GetType("Jellyfin.Server.Program");
                if (programType == null) return null;

                // Try to get _jellyfinHost field (private static)
                var hostField = programType.GetField("_jellyfinHost",
                    System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
                if (hostField == null) return null;

                var host = hostField.GetValue(null);
                if (host == null) return null;

                var servicesProp = host.GetType().GetProperty("Services");
                if (servicesProp == null) return null;

                return servicesProp.GetValue(host) as IServiceProvider;
            }
            catch
            {
                return null;
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
