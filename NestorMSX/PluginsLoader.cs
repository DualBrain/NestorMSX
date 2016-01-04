﻿using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Collections.Generic;
using Konamiman.NestorMSX.Misc;
using Konamiman.Z80dotNet;

namespace Konamiman.NestorMSX
{
    public class PluginsLoader
    {
        private static IDictionary<string, Type> pluginTypes;

        private readonly PluginContext context;
        private readonly Action<string, object[]> tell;

        public PluginsLoader(PluginContext context, Action<string, object[]> tell)
        {
            this.context = context;
            this.tell = tell;
        }

        private IDictionary<string, Type> GetPluginTypes()
        {
            if(pluginTypes != null)
                return pluginTypes;

            var pluginsDirectory = new DirectoryInfo("plugins");

            Func<DirectoryInfo, string[]> getDllFilenames =
                dir => dir.GetFiles("*.dll").Select(f => f.FullName).ToArray();

            var pluginAssemblyFileNames =
                getDllFilenames(pluginsDirectory)
                .Union(pluginsDirectory.GetDirectories().SelectMany(d => getDllFilenames(d)))
                .ToArray();

            pluginTypes =
                pluginAssemblyFileNames.SelectMany(GetPluginsInAssembly)
                    .ToDictionary(x => x.Key, x => x.Value);

            return pluginTypes;
        }

        /// <summary>
        /// Gets an instance of a plugin to be inserted in a slot.
        /// </summary>
        /// <param name="pluginName"></param>
        /// <param name="pluginConfig"></param>
        /// <returns></returns>
        public object GetPluginInstanceForSlot(string pluginName, IDictionary<string, object> pluginConfig)
        {
            var instance = LoadPlugin(pluginName, pluginConfig, requireGetMemory: true);
            if(instance == null)
                throw new InvalidOperationException("The plugin factory method returned null");

            return instance;
        }

        public IEnumerable<object> LoadPlugins(IDictionary<string, object> allConfigValues, IDictionary<string, object> pluginConfigToMerge)
        {
            if (allConfigValues == null)
                ThrowNotValidJson();

            if(!allConfigValues.ContainsKey("plugins"))
                return new object[0];

            var loadedPluginsList = new List<object>();
            
            var commonConfigValues = allConfigValues["sharedPluginsConfig"] as IDictionary<string, object>;
            var plugins = allConfigValues["plugins"] as IDictionary<string, object>;
            if(commonConfigValues == null || plugins == null)
                ThrowNotValidJson();

            var validPluginConfigs =
                plugins
                    .Where(p => p.Value is IDictionary<string, object>)
                    .ToDictionary(p => p.Key, p => (IDictionary<string, object>)p.Value);

            var activePluginConfigs =
                validPluginConfigs.Keys
                    .Where(k => !validPluginConfigs[k].ContainsKey("active") || (validPluginConfigs[k]["active"] as bool?) == true)
                    .ToDictionary(k => k, k => validPluginConfigs[k]);

            var namesOfPluginsConfiguredAsActive = activePluginConfigs.Keys;

            foreach(var pluginName in namesOfPluginsConfiguredAsActive)
            {
                try
                {
                    var pluginConfig = validPluginConfigs[pluginName];

                    pluginConfigToMerge.MergeInto(pluginConfig);

                    foreach(var sharedConfigKey in commonConfigValues.Keys)
                        if(!pluginConfig.ContainsKey(sharedConfigKey))
                            pluginConfig[sharedConfigKey] = commonConfigValues[sharedConfigKey];

                    var pluginInstance = LoadPlugin(pluginName, pluginConfig, requireGetMemory: false);
                    if(pluginInstance != null)
                        loadedPluginsList.Add(pluginInstance);
                }
                catch(Exception ex)
                {
                    tell("Could not load plugin '{0}': {1}", new[] {pluginName, ex.Message});
                }
            }

            return loadedPluginsList.ToArray();
        }

        private static void ThrowNotValidJson()
        {
            throw new InvalidOperationException("plugins.config is not a valid json file");
        }

        private IDictionary<string, Type> GetPluginsInAssembly(string assemblyFileName)
        {
            var assembly = Assembly.LoadFile(assemblyFileName);

            Func<Type, NestorMSXPluginAttribute> getPluginAttribute =
                t => (NestorMSXPluginAttribute)t.GetCustomAttributes(typeof(NestorMSXPluginAttribute), false).SingleOrDefault();

            var pluginTypes =
                assembly.GetTypes()
                    .Where(t => getPluginAttribute(t) != null)
                    .ToArray();

            var pluginTypesByName = pluginTypes.ToDictionary(t => t.FullName);

            foreach(var type in pluginTypes)
            {
                var pluginAttribute = getPluginAttribute(type);
                if(pluginAttribute.Name != null)
                    pluginTypesByName[pluginAttribute.Name] = type;
            }

            return pluginTypesByName;
        }

        private static Type[] argumentsForConstruction = 
            {typeof (PluginContext), typeof (IDictionary<string, object>)};

        private object LoadPlugin(
            string pluginName, 
            IDictionary<string, object> pluginConfig,
            bool requireGetMemory)
        {
            var pluginConfigClone = pluginConfig.Keys.ToDictionary(k => k, k => pluginConfig[k]);

            var pluginTypes = GetPluginTypes();

            if(!pluginTypes.ContainsKey(pluginName))
                throw new InvalidOperationException($"No plugin with name '{pluginName}' found");

            var type = pluginTypes[pluginName];

            var factoryMethod = type
                .GetMethod("GetInstance", BindingFlags.Static | BindingFlags.Public, null,
                    argumentsForConstruction, null);

            if (factoryMethod == null || !factoryMethod.ReturnType.IsAssignableFrom(type))
            {
                var constructor = type.GetConstructor(argumentsForConstruction);
                if(constructor == null)
                    throw new InvalidOperationException("No suitable factory method nor constructor found for " + type.FullName);
            }

            if(requireGetMemory)
            {
                var getMemoryMethod = type.GetMethod("GetMemory");
                if(getMemoryMethod == null || getMemoryMethod.GetParameters().Length > 0 || getMemoryMethod.ReturnType != typeof(IMemory))
                    throw new InvalidOperationException(type.FullName + " has no suitable GetMemory method");
            }

            return 
                factoryMethod == null ? 
                Activator.CreateInstance(type, context, pluginConfigClone) : 
                factoryMethod.Invoke(null, new object[] {context, pluginConfigClone});
        }
    }
}
