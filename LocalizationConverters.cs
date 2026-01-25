using System;
using System.Globalization;
using System.Windows.Data;
using Playnite.SDK;

namespace FastInstall
{
    public class ConflictResolutionToLocalizedStringConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is ConflictResolution cr)
            {
                switch (cr)
                {
                    case ConflictResolution.Ask:
                        return ResourceProvider.GetString("LOCFastInstall_Conflict_Ask");
                    case ConflictResolution.Overwrite:
                        return ResourceProvider.GetString("LOCFastInstall_Conflict_Overwrite");
                    case ConflictResolution.Skip:
                        return ResourceProvider.GetString("LOCFastInstall_Conflict_Skip");
                }
            }

            return value?.ToString() ?? string.Empty;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            // Selection is bound to the enum value directly (SelectedItem),
            // so ConvertBack is not used.
            return Binding.DoNothing;
        }
    }

    /// <summary>
    /// Converter that uses ResourceProvider.GetString() to get localized strings for XAML DynamicResource bindings
    /// </summary>
    public class LocalizedStringConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is string key)
            {
                try
                {
                    return ResourceProvider.GetString(key);
                }
                catch
                {
                    return key; // Return the key if not found
                }
            }
            return value?.ToString() ?? string.Empty;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return Binding.DoNothing;
        }
    }
}

