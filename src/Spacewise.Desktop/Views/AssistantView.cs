using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;

namespace Spacewise.Desktop.Views;

public sealed class AssistantView : UserControl
{
    public AssistantView()
    {

        var content = new StackPanel { Margin = new Thickness(12) };
        var intro = new TextBlock { Text = Strings.Get("AI.Disclaimer"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) };
        content.Children.Add(intro);
        content.Children.Add(new TextBlock
        {
            Text = Strings.Get("AI.FactualAnswers"), TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12),
        });

        var install = new Button
        {
            Padding = new Thickness(14, 8, 14, 8),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        install.SetBinding(ContentControl.ContentProperty, new Binding(nameof(MainViewModel.ModelInstallButtonText)));
        install.SetBinding(IsEnabledProperty, new Binding(nameof(MainViewModel.CanInstallModel)));
        install.SetBinding(Button.CommandProperty, new Binding(nameof(MainViewModel.InstallModelCommand)));
        content.Children.Add(install);

        var cancelDownload = new Button
        {
            Content = Strings.Get("AI.CancelDownload"),
            Padding = new Thickness(14, 8, 14, 8),
            Margin = new Thickness(8, 0, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        cancelDownload.SetBinding(IsEnabledProperty, new Binding(nameof(MainViewModel.IsModelDownloading)));
        cancelDownload.SetBinding(Button.CommandProperty, new Binding(nameof(MainViewModel.InstallModelCancelCommand)));
        content.Children.Add(cancelDownload);

        var download = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
        download.SetBinding(TextBlock.TextProperty, new Binding(nameof(MainViewModel.ModelDownloadStatus)));
        content.Children.Add(download);

        var questionLabel = new TextBlock { Text = Strings.Get("AI.QuestionLabel"), Margin = new Thickness(0, 16, 0, 4) };
        content.Children.Add(questionLabel);
        var question = new TextBox
        {
            MinHeight = 72,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8),
        };
        AutomationProperties.SetName(question, Strings.Get("AI.QuestionLabel"));
        question.SetBinding(TextBox.TextProperty, new Binding(nameof(MainViewModel.AssistantQuestion))
        {
            Mode = BindingMode.TwoWay,
            UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged,
        });
        content.Children.Add(question);

        var explain = new Button
        {
            Content = Strings.Get("AI.ExplainButton"),
            Padding = new Thickness(14, 8, 14, 8),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        explain.SetBinding(IsEnabledProperty, new Binding(nameof(MainViewModel.CanExplainCleanup)));
        explain.SetBinding(Button.CommandProperty, new Binding(nameof(MainViewModel.ExplainCleanupCommand)));
        content.Children.Add(explain);

        var cancelAnalysis = new Button
        {
            Content = Strings.Get("AI.CancelAnalysis"),
            Padding = new Thickness(14, 8, 14, 8),
            Margin = new Thickness(8, 0, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        cancelAnalysis.SetBinding(IsEnabledProperty, new Binding(nameof(MainViewModel.IsAssistantRunning)));
        cancelAnalysis.SetBinding(Button.CommandProperty, new Binding(nameof(MainViewModel.ExplainCleanupCancelCommand)));
        content.Children.Add(cancelAnalysis);

        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
        status.SetBinding(TextBlock.TextProperty, new Binding(nameof(MainViewModel.AssistantStatus)));
        content.Children.Add(status);
        var thinking = new ProgressBar { IsIndeterminate = true, Margin = new Thickness(0, 10, 0, 0) };
        thinking.SetBinding(VisibilityProperty, new Binding(nameof(MainViewModel.IsAssistantRunning))
        {
            Converter = new BooleanToVisibilityConverter(),
        });
        content.Children.Add(thinking);

        var output = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0) };
        output.SetBinding(TextBlock.TextProperty, new Binding(nameof(MainViewModel.AssistantOutput)));
        content.Children.Add(output);

        Content = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = content };

    }
}
