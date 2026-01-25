using System;
using System.Windows;
using System.Windows.Markup;
using Playnite.SDK;

namespace FastInstall
{
    /// <summary>
    /// Markup extension that uses ResourceProvider.GetString() to get localized strings
    /// Usage in XAML: Text="{local:LocalizedString LOCFastInstall_Settings_Title}"
    /// </summary>
    public class LocalizedStringExtension : MarkupExtension
    {
        private static readonly ILogger logger = LogManager.GetLogger();
        public string Key { get; set; }

        public LocalizedStringExtension(string key)
        {
            Key = key;
        }

        public override object ProvideValue(IServiceProvider serviceProvider)
        {
            if (string.IsNullOrWhiteSpace(Key))
                return string.Empty;

            // Try 1: Use Playnite's ResourceProvider.GetString() (preferred method)
            try
            {
                var result = ResourceProvider.GetString(Key);
                if (!string.IsNullOrEmpty(result) && result != Key)
                {
                    return result;
                }
            }
            catch (Exception ex)
            {
                logger.Debug(ex, $"FastInstall: ResourceProvider.GetString() failed for key '{Key}'");
            }

            // Try 2: Look in Application.Resources
            try
            {
                if (Application.Current != null && Application.Current.Resources != null)
                {
                    var resource = Application.Current.Resources[Key];
                    if (resource != null && resource is string str && !string.IsNullOrEmpty(str))
                    {
                        return str;
                    }
                }
            }
            catch (Exception ex)
            {
                logger.Debug(ex, $"FastInstall: Failed to get resource '{Key}' from Application.Resources");
            }

            // Try 3: Look in the target object's resources (if available)
            try
            {
                if (serviceProvider != null)
                {
                    var targetProvider = serviceProvider.GetService(typeof(IProvideValueTarget)) as IProvideValueTarget;
                    if (targetProvider != null)
                    {
                        var targetObject = targetProvider.TargetObject as FrameworkElement;
                        if (targetObject != null)
                        {
                            var resource = targetObject.TryFindResource(Key);
                            if (resource != null && resource is string str && !string.IsNullOrEmpty(str))
                            {
                                return str;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                logger.Debug(ex, $"FastInstall: Failed to get resource '{Key}' from target object resources");
            }

            // Fallback: Return a cleaned version of the key (remove LOCFastInstall_ prefix for readability)
            if (Key.StartsWith("LOCFastInstall_"))
            {
                var cleaned = Key.Substring("LOCFastInstall_".Length).Replace("_", " ");
                logger.Warn($"FastInstall: Localization key '{Key}' not found, returning cleaned key: '{cleaned}'");
                return cleaned;
            }

            return Key;
        }
    }
}
