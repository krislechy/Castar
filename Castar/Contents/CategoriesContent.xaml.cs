using Castar.Api;
using Castar.Compontents;
using Castar.Domain.Models.SettingsModel;
using Castar.ServerConnection;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Castar.Contents;

public partial class CategoriesContent : UserControl
{
    private readonly CastarServerClient server;
    private readonly CategoryScreenState state = new();
    private IReadOnlyList<BudgetCategoryDto> categories = [];
    private Task? refreshTask;
    private CancellationTokenSource? loadRequest;
    private CancellationTokenSource? addRequest;
    private bool refreshNeeded = true;
    private long connectionRevision;
    private long dataRevision;

    public CategoriesContent(CastarServerClient server)
    {
        this.server = server;
        InitializeComponent();
        DataContext = state;
        server.ConnectionChanged += Server_ConnectionChanged;
    }

    internal string SelectKind(string kind)
    {
        var list = kind switch
        {
            BudgetEvents.Income => state.Income,
            BudgetEvents.Purchase => state.Expenses,
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
        CategoryListPanel.Content = list;
        return list.SettingsTitle;
    }

    internal Task RefreshAfterServerChangeAsync()
    {
        refreshNeeded = true;
        return RefreshIfNeededAsync();
    }

    private async void Categories_Loaded(object sender, RoutedEventArgs e) => await RefreshIfNeededAsync();

    private async void Categories_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is true)
            await RefreshIfNeededAsync();
    }

    private async void RetryCategories_Click(object sender, RoutedEventArgs e) =>
        await ButtonLoading.RunAsync(sender, RefreshAfterServerChangeAsync);

    private async void Server_ConnectionChanged(object? sender, EventArgs e)
    {
        connectionRevision++;
        loadRequest?.Cancel();
        addRequest?.Cancel();
        state.HasLoaded = false;
        state.Error = "";
        SetCategories([]);
        await RefreshAfterServerChangeAsync();
    }

    private Task RefreshIfNeededAsync()
    {
        if (!IsLoaded || !IsVisible || !refreshNeeded)
            return Task.CompletedTask;

        if (refreshTask is not { IsCompleted: false })
            refreshTask = LoadCategoriesAsync();

        return refreshTask;
    }

    private async Task LoadCategoriesAsync()
    {
        state.IsLoading = true;
        try
        {
            while (refreshNeeded && IsLoaded && IsVisible)
            {
                refreshNeeded = false;
                var requestedRevision = dataRevision;
                using var request = new CancellationTokenSource();
                loadRequest = request;
                try
                {
                    var response = await server.GetBudgetCategoriesAsync(request.Token);
                    if (!request.IsCancellationRequested && requestedRevision == dataRevision)
                    {
                        SetCategories(response);
                        state.HasLoaded = true;
                        state.Error = "";
                    }
                    else if (!request.IsCancellationRequested)
                    {
                        // A local addition may have raced this snapshot. Fetch once more after it.
                        refreshNeeded = true;
                    }
                }
                catch (OperationCanceledException) when (request.IsCancellationRequested)
                {
                }
                catch (Exception error) when (CastarServerClient.IsConnectionError(error))
                {
                    if (!request.IsCancellationRequested && requestedRevision == dataRevision)
                        state.Error = error is HttpRequestException { StatusCode: HttpStatusCode.NotFound }
                            ? "Обновите Castar Server для работы с категориями."
                            : CastarServerClient.DescribeError(error);
                }
                finally
                {
                    loadRequest = null;
                }
            }
        }
        finally
        {
            state.IsLoading = false;
        }
    }

    private void SetCategories(IReadOnlyList<BudgetCategoryDto> items)
    {
        categories = items;
        state.Income.SetCategories(SortedCategories(BudgetEvents.Income));
        state.Expenses.SetCategories(SortedCategories(BudgetEvents.Purchase));
    }

    private BudgetCategoryDto[] SortedCategories(string kind) => categories
        .Where(category => category.Kind == kind)
        .OrderBy(category => category.Name, StringComparer.CurrentCultureIgnoreCase)
        .ThenBy(category => category.Id)
        .ToArray();

    private async void AddCategory_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: CategoryListState list })
            await OpenCategoryEditorAsync(sender, list.Kind, list.DialogTitle);
    }

    private async void EditCategory_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: BudgetCategoryDto category })
            await OpenCategoryEditorAsync(sender, category.Kind, "Изменить категорию", category);
    }

    private async Task OpenCategoryEditorAsync(object sender, string kind, string title, BudgetCategoryDto? category = null)
    {
        if (!state.CanAdd)
            return;

        state.IsEditing = true;
        var revision = connectionRevision;
        PopupDialog.SetContentWarning(this, null);
        try
        {
            var editor = new BudgetCategoryEditor(kind, category);

            var accepted = await PopupDialog.ShowNested(this, editor, options =>
            {
                options.Title = title;
                options.EditorIcon = BudgetCategoryIcons.ForKey(category?.IconKey);
                options.SuccessButton.SuccessConfirmButtonText = category is null ? "Добавить" : "Сохранить";
                options.AllowMissClick = false;
                return options;
            }, () => editor.CategoryName.Length == 0
                ? new(false, "Укажите название категории.")
                : new(true, null), retainParent: true);

            if (accepted == true && revision == connectionRevision)
                await ButtonLoading.RunAsync(sender, () => SaveCategoryAsync(category?.Id,
                    new BudgetCategoryWriteDto(kind, editor.CategoryName, editor.IconKey)));
        }
        finally
        {
            state.IsEditing = false;
        }
    }

    private async Task SaveCategoryAsync(Guid? id, BudgetCategoryWriteDto draft)
    {
        using var request = new CancellationTokenSource();
        addRequest = request;
        try
        {
            var category = id is { } categoryId
                ? await server.UpdateCategoryAsync(categoryId, draft, request.Token)
                : await server.AddBudgetCategoryAsync(draft, request.Token);
            if (!request.IsCancellationRequested)
            {
                dataRevision++;
                SetCategories(categories.Where(item => item.Id != category.Id).Append(category).ToArray());
            }
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested)
        {
        }
        catch (Exception error) when (CastarServerClient.IsConnectionError(error))
        {
            if (request.IsCancellationRequested)
                return;

            var message = CastarServerClient.DescribeError(error);
            if (Application.Current?.MainWindow is MainWindow { MainDialog.ActiveDialog.Content: CategoriesContent })
                PopupDialog.SetContentWarning(this, message);
            else
                await PopupDialog.Show(message, PopupDialog.TypeDialogEnum.Default, options =>
                {
                    options.Title = "Не удалось сохранить категорию";
                    return options;
                });
        }
        finally
        {
            addRequest = null;
        }
    }

    private sealed class CategoryScreenState : BaseSettingsModel
    {
        private bool hasLoaded;
        private bool isLoading;
        private bool isEditing;
        private string error = "";

        public CategoryListState Income { get; } = new(BudgetEvents.Income, "Доходы", "доходов");
        public CategoryListState Expenses { get; } = new(BudgetEvents.Purchase, "Расходы", "расходов");
        public bool ShowInitialLoading => IsLoading && !HasLoaded;
        public bool CanAdd => HasLoaded && !IsEditing;
        public bool HasError => Error.Length != 0;

        public bool HasLoaded
        {
            get => hasLoaded;
            set
            {
                if (SetField(ref hasLoaded, value))
                {
                    OnPropertyChanged(nameof(ShowInitialLoading));
                    OnPropertyChanged(nameof(CanAdd));
                }
            }
        }

        public bool IsLoading
        {
            get => isLoading;
            set
            {
                if (SetField(ref isLoading, value))
                    OnPropertyChanged(nameof(ShowInitialLoading));
            }
        }

        public bool IsEditing
        {
            get => isEditing;
            set
            {
                if (SetField(ref isEditing, value))
                    OnPropertyChanged(nameof(CanAdd));
            }
        }

        public string Error
        {
            get => error;
            set
            {
                if (SetField(ref error, value))
                    OnPropertyChanged(nameof(HasError));
            }
        }
    }

    private sealed class CategoryListState(string kind, string title, string kindLabel) : BaseSettingsModel
    {
        private BudgetCategoryDto[] categories = [];
        public string Kind { get; } = kind;
        public string Title { get; } = title;
        public string SettingsTitle => $"Категории {kindLabel}";
        public string DialogTitle => $"Категория {kindLabel}";
        public string AddActionName => $"Добавить категорию {kindLabel}";

        public IReadOnlyList<BudgetCategoryDto> Categories => categories;

        internal void SetCategories(BudgetCategoryDto[] value)
        {
            if (!categories.SequenceEqual(value))
                SetField(ref categories, value, nameof(Categories));
        }
    }
}
