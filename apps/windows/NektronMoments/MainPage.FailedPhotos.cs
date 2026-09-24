using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace NektronMoments;

public sealed partial class MainPage
{
    internal ContentDialog CreateFailedPhotoRetryDialog(string activity, int failed, int uncertain)
    {
        var content = new StackPanel { Spacing = 12, MaxWidth = 680 };
        content.Children.Add(new ScrollViewer { MaxHeight = 360, Content = new TextBlock {
            Text = activity, IsTextSelectionEnabled = true, TextWrapping = TextWrapping.Wrap } });
        content.Children.Add(new TextBlock { TextWrapping = TextWrapping.Wrap, Text =
            $"Retry up to {Math.Min(10000, _aiOptions.Limit):N0} failed photos per source, using their original models. No rescan or new-photo backlog. Additional OpenAI API charges apply. Prior cost records are retained." });
        var include = new CheckBox { Name = "RetryUncertainChoice", IsChecked = false,
            IsEnabled = uncertain > 0 && !_importing,
            Content = new TextBlock { TextWrapping = TextWrapping.Wrap, Text = $"Also retry {uncertain:N0} uncertain calls — I accept possible duplicate API charges" } };
        content.Children.Add(include);
        if (_importing) content.Children.Add(new TextBlock { Text = "Finish or stop the current run before retrying failed photos.", TextWrapping = TextWrapping.Wrap });
        var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = "Saved queues", Content = content,
            PrimaryButtonText = "Retry failed photos", CloseButtonText = "Close", DefaultButton = ContentDialogButton.Close,
            RequestedTheme = ActualTheme, IsPrimaryButtonEnabled = !_importing && failed > 0 };
        dialog.Resources["ContentDialogMaxWidth"] = 840d;
        include.Checked += (_, _) => dialog.IsPrimaryButtonEnabled = !_importing && (failed > 0 || uncertain > 0);
        include.Unchecked += (_, _) => dialog.IsPrimaryButtonEnabled = !_importing && failed > 0;
        dialog.PrimaryButtonClick += (_, args) => { if (_importing) { args.Cancel = true; dialog.IsPrimaryButtonEnabled = false; } };
        return dialog;
    }
    private async Task<bool?> ConfirmFailedPhotoRetryAsync(string activity, int failed, int uncertain)
    {
        if (_dialog) return null;
        _dialog = true;
        try {
            var dialog = CreateFailedPhotoRetryDialog(activity, failed, uncertain);
            _commonDialog = dialog;
            var result = await dialog.ShowAsync();
            return result == ContentDialogResult.Primary
                ? ((StackPanel)dialog.Content).Children.OfType<CheckBox>().Single().IsChecked == true : null;
        } finally { _commonDialog = null; _dialog = false; }
    }
    private async Task RetryFailedPhotosAsync(bool includeUncertain)
    {
        if (_importing) { await ShowProcessingProgressAsync(); return; }
        if (_overview is null) await ReloadAsync(false, invalidateThumbnails: false);
        if (_overview is null || _lifetime.IsCancellationRequested) return;
        var sources = _overview.Sources.ToArray();
        if (sources.Length == 0) { StatusText.Text = "No local sources are available for retry."; return; }
        var limit = Math.Min(10000, _aiOptions.Limit);
        await RunMetadataJobAsync(async token => {
            for (var index = 0; index < sources.Length; index++) {
                _metadataProgress!.Source(sources[index].Name, index + 1, sources.Length);
                var arguments = new List<string> { "retry-photos", sources[index].Id, "--limit", limit.ToString(System.Globalization.CultureInfo.InvariantCulture) };
                if (includeUncertain) arguments.Add("--include-uncertain");
                arguments.Add("--no-input");
                await _bridge.RunCliAsync(arguments.ToArray(), line => _metadataProgress?.Report(line), token);
                _metadataProgress.CompleteSource();
            }
        }, sourceNames: sources.Select(source => source.Name).ToArray(), withEnrichment: true, retryFailedPhotos: true);
    }
}
