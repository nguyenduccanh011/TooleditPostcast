#nullable enable
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace PodcastVideoEditor.Ui.Views;

/// <summary>
/// Four-step wizard for quickly creating an episode project.
/// </summary>
public partial class EpisodeWizardDialog : Window
{
    private readonly List<TemplateOption> _choices;
    private int _stepIndex;

    public string ProjectName { get; private set; } = string.Empty;
    public string SelectedTemplateId { get; private set; } = "blank";
    public string SelectedTemplateName { get; private set; } = "Blank";
    public string SelectedTemplateAspectRatio { get; private set; } = "9:16";
    public string SelectedAudioPath { get; private set; } = string.Empty;
    public string ScriptText { get; private set; } = string.Empty;
    public bool UseAiAnalyze { get; private set; }
    public bool OpenRenderDialogAfterSetup { get; private set; }

    public EpisodeWizardDialog(IEnumerable<TemplateOption>? templateOptions = null, string? preselectedTemplateId = null)
    {
        InitializeComponent();

        _choices =
            templateOptions?.ToList()
            ??
            [
                new TemplateOption("blank", "Blank", "Start from an empty layout.", "9:16"),
                new TemplateOption("podcast_9x16", "Podcast Vertical", "Vertical short-form podcast frame with title and branding defaults.", "9:16"),
                new TemplateOption("podcast_16x9", "Podcast Landscape", "Landscape layout for YouTube-style episodes.", "16:9"),
                new TemplateOption("podcast_1x1", "Podcast Square", "Square social format with centered visual space.", "1:1"),
            ];

        TemplateComboBox.ItemsSource = _choices;
        var selected = !string.IsNullOrWhiteSpace(preselectedTemplateId)
            ? _choices.FirstOrDefault(x => string.Equals(x.Id, preselectedTemplateId, StringComparison.OrdinalIgnoreCase))
            : null;

        if (selected != null)
            TemplateComboBox.SelectedItem = selected;
        else
            TemplateComboBox.SelectedIndex = _choices.Count > 1 ? 1 : 0;

        ProjectNameTextBox.Text = $"Episode {DateTime.Now:yyyy-MM-dd}";

        UpdateUi();
    }

    private void TemplateComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateTemplatePreview();
        UpdateSummary();
    }

    private void BrowseAudioButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select main audio file",
            Filter = "Audio Files (*.mp3;*.wav;*.m4a;*.aac;*.flac;*.ogg)|*.mp3;*.wav;*.m4a;*.aac;*.flac;*.ogg|All files (*.*)|*.*",
            CheckFileExists = true,
        };

        if (dialog.ShowDialog() != true)
            return;

        AudioPathTextBox.Text = dialog.FileName;
        AudioValidationText.Text = File.Exists(dialog.FileName)
            ? "Audio file is valid."
            : "Selected file does not exist.";

        UpdateSummary();
        ClearValidation();
    }

    private void LoadScriptButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select script text file",
            Filter = "Text files (*.txt)|*.txt|All files (*.*)|*.*",
            CheckFileExists = true,
        };

        if (dialog.ShowDialog() != true)
            return;

        try
        {
            ScriptTextBox.Text = File.ReadAllText(dialog.FileName);
            ClearValidation();
        }
        catch (Exception ex)
        {
            ShowValidation($"Cannot read script file: {ex.Message}");
        }

        UpdateSummary();
    }

    private void ClearScriptButton_Click(object sender, RoutedEventArgs e)
    {
        ScriptTextBox.Text = string.Empty;
        UpdateSummary();
        ClearValidation();
    }

    private void BackButton_Click(object sender, RoutedEventArgs e)
    {
        if (_stepIndex <= 0)
            return;

        _stepIndex--;
        UpdateUi();
    }

    private void NextButton_Click(object sender, RoutedEventArgs e)
    {
        if (!ValidateStep())
            return;

        if (_stepIndex >= 3)
            return;

        _stepIndex++;
        UpdateUi();
    }

    private void FinishButton_Click(object sender, RoutedEventArgs e)
    {
        if (!ValidateAllSteps())
            return;

        ProjectName = ProjectNameTextBox.Text.Trim();
        var selectedTemplate = TemplateComboBox.SelectedItem as TemplateOption ?? _choices[0];

        SelectedTemplateId = selectedTemplate.Id;
        SelectedTemplateName = selectedTemplate.DisplayName;
        SelectedTemplateAspectRatio = selectedTemplate.AspectRatio;
        SelectedAudioPath = AudioPathTextBox.Text.Trim();
        ScriptText = ScriptTextBox.Text;
        UseAiAnalyze = UseAiCheckBox.IsChecked == true;
        OpenRenderDialogAfterSetup = OpenRenderDialogCheckBox.IsChecked == true;

        DialogResult = true;
        Close();
    }

    private bool ValidateAllSteps()
    {
        var initialStep = _stepIndex;

        for (var i = 0; i < 4; i++)
        {
            _stepIndex = i;
            if (!ValidateStep())
            {
                UpdateUi();
                return false;
            }
        }

        _stepIndex = initialStep;
        ClearValidation();
        return true;
    }

    private bool ValidateStep()
    {
        ClearValidation();

        switch (_stepIndex)
        {
            case 0:
                if (string.IsNullOrWhiteSpace(ProjectNameTextBox.Text))
                {
                    ShowValidation("Project name is required.");
                    return false;
                }

                if (TemplateComboBox.SelectedItem is null)
                {
                    ShowValidation("Please choose a template.");
                    return false;
                }

                return true;

            case 1:
                var audioPath = AudioPathTextBox.Text.Trim();
                if (string.IsNullOrWhiteSpace(audioPath))
                {
                    ShowValidation("Please select an audio file.");
                    return false;
                }

                if (!File.Exists(audioPath))
                {
                    ShowValidation("Selected audio file does not exist.");
                    return false;
                }

                return true;

            case 2:
                // Script is optional for audio-only workflows.
                return true;

            case 3:
                return true;

            default:
                return true;
        }
    }

    private void UpdateUi()
    {
        Step1Panel.Visibility = _stepIndex == 0 ? Visibility.Visible : Visibility.Collapsed;
        Step2Panel.Visibility = _stepIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
        Step3Panel.Visibility = _stepIndex == 2 ? Visibility.Visible : Visibility.Collapsed;
        Step4Panel.Visibility = _stepIndex == 3 ? Visibility.Visible : Visibility.Collapsed;

        BackButton.IsEnabled = _stepIndex > 0;
        NextButton.Visibility = _stepIndex < 3 ? Visibility.Visible : Visibility.Collapsed;
        FinishButton.Visibility = _stepIndex == 3 ? Visibility.Visible : Visibility.Collapsed;

        SetStepBadgeColors();
        UpdateTemplatePreview();
        UpdateSummary();
    }

    private void SetStepBadgeColors()
    {
        SetBadgeColor(Step1Badge, _stepIndex == 0);
        SetBadgeColor(Step2Badge, _stepIndex == 1);
        SetBadgeColor(Step3Badge, _stepIndex == 2);
        SetBadgeColor(Step4Badge, _stepIndex == 3);
    }

    private static void SetBadgeColor(Border badge, bool isActive)
    {
        badge.Background = isActive
            ? new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#2EA6FF"))
            : new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#2D2D30"));
    }

    private void UpdateTemplatePreview()
    {
        var selectedTemplate = TemplateComboBox.SelectedItem as TemplateOption ?? _choices[0];
        TemplateTitleText.Text = selectedTemplate.DisplayName;
        TemplateDescriptionText.Text = selectedTemplate.Description;
        TemplateAspectText.Text = $"Aspect ratio: {selectedTemplate.AspectRatio}";
    }

    private void UpdateSummary()
    {
        var selectedTemplate = TemplateComboBox.SelectedItem as TemplateOption ?? _choices[0];
        var scriptLength = string.IsNullOrWhiteSpace(ScriptTextBox.Text) ? "Skipped" : $"{ScriptTextBox.Text.Length} chars";
        var audioText = string.IsNullOrWhiteSpace(AudioPathTextBox.Text) ? "Not selected" : Path.GetFileName(AudioPathTextBox.Text);

        SummaryProjectText.Text = $"Project: {ProjectNameTextBox.Text.Trim()}";
        SummaryTemplateText.Text = $"Template: {selectedTemplate.DisplayName}";
        SummaryAudioText.Text = $"Audio: {audioText}";
        SummaryScriptText.Text = $"Script: {scriptLength}, AI: {(UseAiCheckBox.IsChecked == true ? "On" : "Off")}";
        SummaryAspectText.Text = $"Target aspect ratio: {selectedTemplate.AspectRatio}";
    }

    private void ShowValidation(string message)
    {
        ValidationTextBlock.Text = message;
    }

    private void ClearValidation()
    {
        ValidationTextBlock.Text = string.Empty;
    }

    public sealed record TemplateOption(string Id, string DisplayName, string Description, string AspectRatio);
}
