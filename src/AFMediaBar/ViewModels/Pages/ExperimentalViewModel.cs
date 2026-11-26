// 协调实验开关与保存的宿主偏好，不直接创建窗口或执行音频操作。
using AFMediaBar.Classes.Abstractions;
using AFMediaBar.Classes.Settings;

namespace AFMediaBar.ViewModels.Pages;

/// <summary>实验页状态，关闭项目保留其配置。</summary>
public sealed class ExperimentalViewModel : ObservableObject, IDisposable
{
    private readonly ISettingsConfiguration _configuration;
    public ExperimentalViewModel(ISettingsConfiguration configuration)
    {
        _configuration = configuration;
        SettingsManager.SettingsChanged += OnSettingsChanged;
    }

    public bool SystemMemoryCleanup
    {
        get => SettingsManager.Current.Experiments.SystemMemoryCleanup;
        set => _configuration.SetExperiments(SettingsManager.Current.Experiments with { SystemMemoryCleanup = value });
    }
    public bool MemoryTools
    {
        get => SettingsManager.Current.Experiments.MemoryTools;
        set => _configuration.SetExperiments(SettingsManager.Current.Experiments with { MemoryTools = value });
    }
    public bool BluetoothBattery
    {
        get => SettingsManager.Current.Experiments.BluetoothBattery;
        set => _configuration.SetExperiments(SettingsManager.Current.Experiments with { BluetoothBattery = value });
    }
    public bool NonsPlayerQueue
    {
        get => SettingsManager.Current.Experiments.NonsPlayerQueue;
        set => _configuration.SetExperiments(SettingsManager.Current.Experiments with { NonsPlayerQueue = value });
    }
    private void OnSettingsChanged(object? sender, SettingsChangedEventArgs args)
    {
        if (args.PropertyName is null or nameof(AppSettings.Experiments) or nameof(AppSettings.FloatingBar) or nameof(AppSettings.WindowMode))
        {
            OnPropertyChanged(nameof(SystemMemoryCleanup));
            OnPropertyChanged(nameof(MemoryTools));
            OnPropertyChanged(nameof(BluetoothBattery));
            OnPropertyChanged(nameof(NonsPlayerQueue));
        }
    }
    public void Dispose()
    {
        SettingsManager.SettingsChanged -= OnSettingsChanged;
    }
}
