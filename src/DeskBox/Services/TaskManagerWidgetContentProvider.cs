using DeskBox.Contracts;
using DeskBox.Controls.WidgetContents;
using DeskBox.Models;
using DeskBox.Services;
using DeskBox.ViewModels;

namespace DeskBox.Services;

/// <summary>
/// Content provider for the Task Manager feature widget (WidgetKind.TaskManager).
/// Exposes the process table through the shared detached-content pipeline.
/// </summary>
internal sealed class TaskManagerWidgetContentProvider : IWidgetContentProvider
{
    public WidgetKind WidgetKind => WidgetKind.TaskManager;

    public bool CanCreateDetachedContent => true;

    public IWidgetContent CreateDetachedContent(WidgetConfig config, WidgetContentProviderContext context)
    {
        if (config.WidgetKind != WidgetKind)
        {
            throw new ArgumentException("Task Manager content requires a Task Manager widget config.", nameof(config));
        }

        return context.SettingsService is null
            ? new TaskManagerWidgetContentAdapter(config, context.LocalizationService)
            : new TaskManagerWidgetContentAdapter(config, context.LocalizationService, context.SettingsService);
    }
}
