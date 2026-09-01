using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Xunit;

namespace VNotch.Tests;

public sealed class VisibleExitControlTests
{
    [Fact]
    public void StatusBarPlacesPowerControlImmediatelyAfterSettings()
    {
        string repositoryRoot = FindRepositoryRoot();
        var document = XDocument.Load(Path.Combine(repositoryRoot, "Windows", "MainWindow.xaml"));
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

        XElement statusBar = document.Descendants(presentation + "Grid")
            .Single(element => (string?)element.Attribute(xaml + "Name") == "StatusBar");
        XElement columnDefinitions = statusBar.Element(presentation + "Grid.ColumnDefinitions")!;
        Assert.Equal(4, columnDefinitions.Elements(presentation + "ColumnDefinition").Count());

        XElement settingsButton = statusBar.Descendants(presentation + "Border")
            .Single(element => (string?)element.Attribute(xaml + "Name") == "SettingsButton");
        XElement exitButton = statusBar.Descendants(presentation + "Border")
            .Single(element => (string?)element.Attribute(xaml + "Name") == "ExitButton");

        Assert.Equal("2", (string?)settingsButton.Attribute("Grid.Column"));
        Assert.Equal("3", (string?)exitButton.Attribute("Grid.Column"));
        Assert.Equal((string?)settingsButton.Attribute("Width"), (string?)exitButton.Attribute("Width"));
        Assert.Equal((string?)settingsButton.Attribute("Height"), (string?)exitButton.Attribute("Height"));
        Assert.Equal((string?)settingsButton.Attribute("CornerRadius"), (string?)exitButton.Attribute("CornerRadius"));
        Assert.Equal("7,0,0,0", (string?)exitButton.Attribute("Margin"));
        Assert.Equal("Exit V-Notch", (string?)exitButton.Attribute("ToolTip"));
        Assert.Equal("ExitButton_Click", (string?)exitButton.Attribute("MouseLeftButtonDown"));

        XElement powerIcon = exitButton.Descendants(presentation + "TextBlock").Single();
        Assert.Equal("\uE7E8", (string?)powerIcon.Attribute("Text"));
    }

    [Fact]
    public void VisibleExitAndTrayExitShareTheCleanupShutdownPath()
    {
        string repositoryRoot = FindRepositoryRoot();
        var document = XDocument.Load(Path.Combine(repositoryRoot, "Windows", "MainWindow.xaml"));
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        Assert.Single(document.Descendants(presentation + "MenuItem")
            .Where(element => (string?)element.Attribute("Click") == "Exit_Click"));

        string source = File.ReadAllText(Path.Combine(repositoryRoot, "Windows", "MainWindow.xaml.cs"));
        Assert.Contains("private void ExitButton_Click(object sender, MouseButtonEventArgs e)", source);
        Assert.Contains("private void Exit_Click(object sender, RoutedEventArgs e)", source);

        int trayExitStart = source.IndexOf("private void Exit_Click(object sender, RoutedEventArgs e)", StringComparison.Ordinal);
        int visibleExitStart = source.IndexOf("private void ExitButton_Click", trayExitStart, StringComparison.Ordinal);
        Assert.True(trayExitStart >= 0 && visibleExitStart > trayExitStart);
        Assert.Contains("ShutdownApplication();", source[trayExitStart..visibleExitStart]);

        int shutdownMethodStart = source.IndexOf("private void ShutdownApplication()", StringComparison.Ordinal);
        Assert.True(shutdownMethodStart >= 0);
        string shutdownMethod = source[shutdownMethodStart..];
        Assert.Contains("CleanupBeforeShutdown();", shutdownMethod);
        Assert.Contains("System.Windows.Application.Current.Shutdown();", shutdownMethod);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "V-Notch.csproj")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the V-Notch repository root.");
    }
}
