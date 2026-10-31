using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using OpenPet.App.Pet;
using OpenPet.App.Services;

namespace OpenPet.App;

/// <summary>动作/表情包素材库：浏览全部动作并点播，或把表情包投到小屏。</summary>
public partial class LibraryWindow : Window
{
    private readonly PetRuntime _runtime;
    private readonly Random _random = new();

    public LibraryWindow(PetRuntime runtime)
    {
        InitializeComponent();
        _runtime = runtime;

        ActionList.ItemsSource = runtime.ActionNames;
        MemeList.ItemsSource = runtime.MemeNames;
        CountsText.Text = $"动作 {runtime.ActionNames.Count} 个 · 表情包 {runtime.MemeNames.Count} 张";

        if (ActionList.Items.Count > 0)
        {
            ActionList.SelectedIndex = 0;
        }

        if (MemeList.Items.Count > 0)
        {
            MemeList.SelectedIndex = 0;
        }
    }

    private void ActionList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ActionList.SelectedItem is not string name)
        {
            return;
        }

        ActionNameText.Text = name;
        ActionStatusText.Text = "";
        PlayActionButton.IsEnabled = true;
    }

    private void ActionList_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (ActionList.SelectedItem is string)
        {
            PlaySelectedAction();
        }
    }

    private void PlayAction_Click(object sender, RoutedEventArgs e) => PlaySelectedAction();

    private void PlaySelectedAction()
    {
        if (ActionList.SelectedItem is not string name)
        {
            return;
        }

        PetActionSummary result = _runtime.PlayAction(name);
        ActionStatusText.Text = result.Ok ? $"正在播放「{name}」" : result.Message;
    }

    private void RandomAction_Click(object sender, RoutedEventArgs e)
    {
        int count = ActionList.Items.Count;
        if (count == 0)
        {
            return;
        }

        ActionList.SelectedIndex = _random.Next(count);
        PlaySelectedAction();
    }

    private void MemeList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (MemeList.SelectedItem is not string name)
        {
            return;
        }

        MemeNameText.Text = name;
        MemeStatusText.Text = "";
        PlayMemeButton.IsEnabled = true;

        string? path = _runtime.GetMemePath(name);
        if (path == null)
        {
            MemePreview.Source = null;
            return;
        }

        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.UriSource = new Uri(path, UriKind.Absolute);
            bitmap.EndInit();
            bitmap.Freeze();
            MemePreview.Source = bitmap;
        }
        catch
        {
            MemePreview.Source = null;
        }
    }

    private void PlayMeme_Click(object sender, RoutedEventArgs e)
    {
        if (MemeList.SelectedItem is not string name)
        {
            return;
        }

        bool ok = _runtime.PlayMeme(name);
        MemeStatusText.Text = ok ? $"已投到屏幕：{name}" : "投屏失败（检查素材或引擎是否已初始化）";
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
