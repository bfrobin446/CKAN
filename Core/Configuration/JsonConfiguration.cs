﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;

namespace CKAN.Configuration
{
    public class JsonConfiguration : IConfiguration
    {

        #region JSON Structures

        private class Config
        {
            public string AutoStartInstance { get; set; }
            public string DownloadCacheDir { get; set; }
            public long? CacheSizeLimit { get; set; }
            public int? RefreshRate { get; set; }
            public JBuilds KSPBuilds { get; set; }
            public IList<KspInstance> KspInstances = new List<KspInstance>();
            public IDictionary<string, string> AuthTokens = new Dictionary<string, string>();
        }

        private class KspInstance
        {
            public string Name { get; set; }
            public string Path { get; set; }
        }

        #endregion

        // The standard location of the config file. Where this actually points is platform dependent,
        // but it's the same place as the downloads folder. The location can be overwritten with the
        // CKAN_CONFIG_FILE environment variable.
        public static readonly string defaultConfigFile =
            Environment.GetEnvironmentVariable("CKAN_CONFIG_FILE")
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "CKAN",
                "config.json"
            );

        public static readonly string DefaultDownloadCacheDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CKAN",
            "downloads"
        );

        // The actual config file state, and it's location on the disk (we allow
        // the location to be changed for unit tests). Note that these are static
        // because we only want to have one copy of the config file in memory. This
        // version is considered authoritative, and we save it to the disk every time
        // it gets changed.
        //
        // If you have multiple instances of CKAN running at the same time, each will
        // believe that their copy of the config file in memory is authoritative, so
        // changes made by one copy will not be respected by the other.
        //
        // Since we only have one copy in memory, we need to use _lock in order to
        // keep things consistent. Depending on performance needs, it may make sense
        // to switch to a read/write lock---but only do that after profiling. It is
        // almost certainly more effort than it's worth, and may not actually provide
        // any performance gains.
        private static readonly object _lock = new object();
        private static string configFile = defaultConfigFile;
        private static Config config = null;

        // <summary>
        // Where the config file is located.
        // </summary>
        public string ConfigFile
        {
            get => configFile;
        }

        public string DownloadCacheDir
        {
            get
            {
                lock (_lock)
                {
                    return config.DownloadCacheDir ?? DefaultDownloadCacheDir;
                }
            }
            set
            {
                lock (_lock)
                {
                    if (string.IsNullOrEmpty(value))
                    {
                        config.DownloadCacheDir = null;
                    }
                    else
                    {
                        if (!Path.IsPathRooted(value))
                        {
                            value = Path.GetFullPath(value);
                        }
                        config.DownloadCacheDir = value;
                    }

                    SaveConfig();
                }
            }
        }

        public long? CacheSizeLimit
        {
            get
            {
                lock (_lock)
                {
                    return config.CacheSizeLimit;
                }
            }

            set
            {
                lock (_lock)
                {
                    if (value < 0)
                    {
                        config.CacheSizeLimit = null;
                    }
                    else
                    {
                        config.CacheSizeLimit = value;
                    }

                    SaveConfig();
                }
            }
        }

        public int RefreshRate
        {
            get
            {
                lock (_lock)
                {
                    return config.RefreshRate ?? 0;
                }
            }

            set
            {
                lock (_lock)
                {
                    if (value <= 0)
                    {
                        config.RefreshRate = null;
                    }
                    else
                    {
                        config.RefreshRate = value;
                    }

                    SaveConfig();
                }
            }
        }


        public string AutoStartInstance
        {
            get
            {
                lock (_lock)
                {
                    return config.AutoStartInstance ?? "";
                }
            }

            set
            {
                lock (_lock)
                {
                    config.AutoStartInstance = value ?? "";

                    SaveConfig();
                }
            }
        }

        // <summary>
        // Create a new instance of JsonConfiguration. ServiceLocator maintains a
        // singleton instance, so in general you should use that. However, the
        // core state is static, so creating multiple instances is not an issue.
        // </summary>
        public JsonConfiguration()
        {
            lock (_lock)
            {
                if (config != null)
                    return;

                LoadConfig();
            }
        }

        // <summary>
        // For testing purposes only. This constructor discards the global configuration
        // state, and recreates it from the specified file.
        // </summary>
        //
        // N.B., if you're adding the ability to specify a config file from the CLI, this
        // might be the way to do it. However, you need to ensure that the configuration
        // doesn't get loaded from the default location first, as that might end up
        // creating files and directories that the user is trying to avoid creating by
        // specifying the configuration file on the command line.
        public JsonConfiguration(string newConfig)
        {
            lock (_lock)
            {
                configFile = newConfig;

                LoadConfig();
            }
        }


        public JBuilds GetKSPBuilds()
        {
            lock (_lock)
            {
                return config.KSPBuilds;
            }
        }

        public void SetKSPBuilds(JBuilds buildMap)
        {
            lock (_lock)
            {
                config.KSPBuilds = buildMap;

                SaveConfig();
            }
        }

        public IEnumerable<Tuple<string, string>> GetInstances()
        {
            lock (_lock)
            {
                return config.KspInstances.Select(instance =>
                    new Tuple<string, string>(instance.Name, instance.Path));
            }
        }

        public void SetRegistryToInstances(SortedList<string, KSP> instances)
        {
            lock (_lock)
            {
                config.KspInstances = instances.Select(instance => new KspInstance
                {
                    Name = instance.Key,
                    Path = instance.Value.GameDir()
                }).ToList();

                SaveConfig();
            }
        }

        public IEnumerable<string> GetAuthTokenHosts()
        {
            lock (_lock)
            {
                return config.AuthTokens.Keys;
            }
        }


        public bool TryGetAuthToken(string host, out string token)
        {
            lock (_lock)
            {
                return config.AuthTokens.TryGetValue(host, out token);
            }
        }

        public void SetAuthToken(string host, string token)
        {
            lock (_lock)
            {
                config.AuthTokens[host] = token;

                SaveConfig();
            }
        }

        // <summary>
        // Save the JSON configuration file.
        // </summary>
        private static void SaveConfig()
        {
            lock (_lock)
            {
                string json = JsonConvert.SerializeObject(config, Formatting.Indented);
                File.WriteAllText(configFile, json);
            }
        }

        // <summary>
        // Load the JSON configuration file. This will replace the current state.
        //
        // If the configuration file does not exist, this will create it and then
        // try to populate it with values in the registry left from the old system.
        // </summary>
        private void LoadConfig()
        {
            lock (_lock)
            {
                try
                {
                    string json = File.ReadAllText(configFile);
                    config = JsonConvert.DeserializeObject<Config>(json);

                    if (config == null)
                    {
                        config = new Config();
                    }

                    if (config.KspInstances == null)
                    {
                        config.KspInstances = new List<KspInstance>();
                    }

                    if (config.AuthTokens == null)
                    {
                        config.AuthTokens = new Dictionary<string, string>();
                    }
                }
                catch (Exception ex) when (ex is FileNotFoundException || ex is DirectoryNotFoundException)
                {
                    // This runs if the configuration does not exist. We will create a new configuration and
                    // try to migrate from the registry.
                    config = new Config();

                    // Ensure the directory exists
                    new FileInfo(configFile).Directory.Create();

#if !NETSTANDARD
                    // If we are not running on .NET Standard, try to migrate from the real registry
                    if (Win32RegistryConfiguration.DoesRegistryConfigurationExist())
                    {
                        Migrate();

                        // TODO: At some point, we can uncomment this to clean up after ourselves.
                        // Win32RegistryConfiguration.DeleteAllKeys();
                    }
#endif

                    SaveConfig();
                }
            }
        }

        // <summary>
        // Copy the configuration from the registry here.
        // </summary>
        private void Migrate()
        {
            Win32RegistryConfiguration registry = new Win32RegistryConfiguration();

            lock (_lock)
            {
                var instances = registry.GetInstances();
                config.KspInstances = instances.Select(instance => new KspInstance
                {
                    Name = instance.Item1,
                    Path = instance.Item2
                }).ToList();

                config.KSPBuilds = registry.GetKSPBuilds();

                config.AutoStartInstance = registry.AutoStartInstance;
                config.DownloadCacheDir = registry.DownloadCacheDir;
                config.CacheSizeLimit = registry.CacheSizeLimit;
                config.RefreshRate = registry.RefreshRate;

                foreach (string host in registry.GetAuthTokenHosts())
                {
                    if (registry.TryGetAuthToken(host, out string token))
                    {
                        config.AuthTokens[host] = token;
                    }
                }

                SaveConfig();
            }
        }
    }
}
