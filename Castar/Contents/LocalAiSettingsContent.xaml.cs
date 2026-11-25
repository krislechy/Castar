using Castar.Compontents;
using Castar.LocalAi;
using Castar.Settings;
using Microsoft.Win32;
using System;
using System.Windows;
using System.Windows.Controls;

namespace Castar.Contents;

public partial class LocalAiSettingsContent : UserControl
{
    private readonly LocalAiService service;
    private readonly LocalAiSettingsViewModel viewModel;

    internal LocalAiSettingsContent(ClientSettings settings, LocalAiService service)
    {
        this.service = service;
        viewModel = new LocalAiSettingsViewModel(settings, service);
        InitializeComponent();
        DataContext = viewModel;
    }

    private void LocalAiSettings_Loaded(object sender, RoutedEventArgs e)
    {
        service.ProcessStateChanged += Service_ProcessStateChanged;
        if (IsVisible)
            viewModel.BeginEditing();
    }

    private void LocalAiSettings_Unloaded(object sender, RoutedEventArgs e)
    {
        service.ProcessStateChanged -= Service_ProcessStateChanged;
        viewModel.EndEditing();
    }

    private void LocalAiSettings_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is true && IsLoaded)
            viewModel.BeginEditing();
        else if (e.NewValue is false)
            viewModel.EndEditing();
    }

    private void BrowseExecutable_Click(object sender, RoutedEventArgs e)
    {
        var selector = new OpenFileDialog
        {
            Title = "Исполняемый файл",
            Filter = "Программы (*.exe)|*.exe",
            CheckFileExists = true
        };
        if (selector.ShowDialog(Window.GetWindow(this)) == true)
            viewModel.ExecutablePath = selector.FileName;
    }

    private void BrowseDirectory_Click(object sender, RoutedEventArgs e)
    {
        var selector = new OpenFolderDialog { Title = "Папка моделей GGUF" };
        if (selector.ShowDialog(Window.GetWindow(this)) == true)
            viewModel.ModelDirectory = selector.FolderName;
    }

    private async void DiscoverModels_Click(object sender, RoutedEventArgs e) =>
        await ButtonLoading.RunAsync(sender, viewModel.DiscoverAsync);

    private async void StartProcess_Click(object sender, RoutedEventArgs e) =>
        await ButtonLoading.RunAsync(sender, viewModel.StartProcessAsync);

    private async void StopProcess_Click(object sender, RoutedEventArgs e) =>
        await ButtonLoading.RunAsync(sender, viewModel.StopProcessAsync);

    private void Apply_Click(object sender, RoutedEventArgs e) => viewModel.Apply();

    private void Service_ProcessStateChanged(object? sender, EventArgs e)
    {
        if (Dispatcher.CheckAccess())
            viewModel.NotifyProcessState();
        else if (!Dispatcher.HasShutdownStarted)
            _ = Dispatcher.InvokeAsync(viewModel.NotifyProcessState);
    }
}
