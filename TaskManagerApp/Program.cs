using System.Diagnostics;
using System.Text.Json;
using Serilog;
using Serilog.Events;
using Serilog.Formatting.Json;

internal sealed class ExceptionContext
{
    public required string Operation { get; init; }
    public required string Message { get; init; }
    public required string StackTrace { get; init; }
    public LogEventLevel Level { get; init; } = LogEventLevel.Error;
}

internal static class AppTracing
{
    private static readonly TraceSource Trace = new("TaskManagerTrace", SourceLevels.All);
    private static bool _isInitialized;

    public static void Initialize()
    {
        if (_isInitialized)
        {
            return;
        }

        var logsDirectory = Path.Combine(AppContext.BaseDirectory, "logs");
        Directory.CreateDirectory(logsDirectory);

        Trace.Listeners.Clear();
        Trace.Listeners.Add(new TextWriterTraceListener(Path.Combine(logsDirectory, "taskmanager-trace.log")));
        Trace.Switch = new SourceSwitch("TaskManagerSwitch", "All");
        System.Diagnostics.Trace.AutoFlush = true;

        _isInitialized = true;
        Trace.TraceEvent(TraceEventType.Start, 1000, "Трассировка инициализирована");
        Trace.Flush();
    }

    public static IDisposable BeginOperation(string operationName)
    {
        if (!_isInitialized)
        {
            Initialize();
        }

        return new TraceScope(operationName);
    }

    public static void TraceInformation(string message) => Trace.TraceInformation(message);

    public static void TraceError(Exception ex, string operationName)
    {
        Trace.TraceEvent(TraceEventType.Error, 5000, "Ошибка в {0}: {1}", operationName, ex.Message);
        Trace.Flush();
    }

    public static void EndSession()
    {
        if (!_isInitialized)
        {
            return;
        }

        Trace.TraceEvent(TraceEventType.Stop, 1999, "Сеанс трассировки завершен");
        Trace.Flush();
        Trace.Close();
        _isInitialized = false;
    }

    private sealed class TraceScope : IDisposable
    {
        private readonly string _operationName;
        private readonly Stopwatch _stopwatch;
        private bool _disposed;

        public TraceScope(string operationName)
        {
            _operationName = operationName;
            _stopwatch = Stopwatch.StartNew();
            Trace.TraceEvent(TraceEventType.Start, 2000, "Начало операции {0}", _operationName);
            Trace.Flush();
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _stopwatch.Stop();
            Trace.TraceEvent(
                TraceEventType.Stop,
                2001,
                "Завершение операции {0}. Время выполнения: {1} ms",
                _operationName,
                _stopwatch.ElapsedMilliseconds);
            Trace.Flush();
            _disposed = true;
        }
    }
}

internal static class ExceptionHandler
{
    public static bool ShouldUseExternalAlerts { get; set; }

    public static void Handle(
        Exception ex,
        string operation,
        LogEventLevel level = LogEventLevel.Error,
        bool notifyInConsole = true)
    {
        var context = new ExceptionContext
        {
            Operation = operation,
            Message = ex.Message,
            StackTrace = ex.StackTrace ?? "StackTrace is empty.",
            Level = level
        };

        LogAtLevel(ex, context);

        if (notifyInConsole)
        {
            NotifyConsole(context);
        }

        if (level >= LogEventLevel.Fatal && ShouldUseExternalAlerts)
        {
            NotifyExternalSystems(context);
        }
    }

    private static void LogAtLevel(Exception ex, ExceptionContext context)
    {
        var logger = Log.ForContext("Operation", context.Operation)
            .ForContext("ErrorMessage", context.Message)
            .ForContext("ErrorStackTrace", context.StackTrace)
            .ForContext("ErrorLevel", context.Level.ToString());

        switch (context.Level)
        {
            case LogEventLevel.Fatal:
                logger.Fatal(ex, "Unhandled exception during {Operation}", context.Operation);
                break;
            case LogEventLevel.Error:
                logger.Error(ex, "Exception during {Operation}", context.Operation);
                break;
            case LogEventLevel.Warning:
                logger.Warning(ex, "Warning-level exception during {Operation}", context.Operation);
                break;
            default:
                logger.Information(ex, "Exception during {Operation}", context.Operation);
                break;
        }
    }

    private static void NotifyConsole(ExceptionContext context)
    {
        var previousColor = Console.ForegroundColor;
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"!!! Ошибка !!! Операция: {context.Operation}. Уровень: {context.Level}");
        Console.ForegroundColor = previousColor;

        Log.Warning("Показано уведомление в консоли для операции {Operation}", context.Operation);
    }

    private static void NotifyExternalSystems(ExceptionContext context)
    {
        // Здесь можно подключить Sentry, Application Insights или email-оповещения.
        Log.Warning(
            "External alert placeholder called for critical issue. Operation: {Operation}, Message: {Message}",
            context.Operation,
            context.Message);
    }
}

internal sealed class TaskItem
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Title { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public TaskPriority Priority { get; set; } = TaskPriority.Medium;

    public bool IsCompleted { get; set; }

    public override string ToString() => Title;
}

internal enum TaskPriority
{
    Low,
    Medium,
    High
}

internal sealed class TaskStorageService
{
    private readonly string _filePath;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public TaskStorageService(string filePath)
    {
        _filePath = filePath;
    }

    public List<TaskItem> LoadTasks()
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            if (!File.Exists(_filePath))
            {
                Log.Information("Файл задач не найден, будет создан при первом сохранении: {Path}", _filePath);
                return [];
            }

            var json = File.ReadAllText(_filePath);
            var tasks = JsonSerializer.Deserialize<List<TaskItem>>(json) ?? [];
            stopwatch.Stop();
            Log.Information("Загружено задач: {Count}. Время: {Time} ms", tasks.Count, stopwatch.ElapsedMilliseconds);
            return tasks;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            ExceptionHandler.Handle(ex, $"LoadTasks({_filePath})", LogEventLevel.Error);
            return [];
        }
    }

    public bool SaveTasks(IReadOnlyCollection<TaskItem> tasks, out string errorMessage)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var directoryPath = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrWhiteSpace(directoryPath))
            {
                Directory.CreateDirectory(directoryPath);
            }

            var json = JsonSerializer.Serialize(tasks, JsonOptions);
            File.WriteAllText(_filePath, json);
            stopwatch.Stop();
            errorMessage = string.Empty;
            Log.Information("Сохранено задач: {Count}. Время: {Time} ms", tasks.Count, stopwatch.ElapsedMilliseconds);
            return true;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            errorMessage = "Не удалось сохранить задачи на диск.";
            ExceptionHandler.Handle(ex, $"SaveTasks({_filePath})", LogEventLevel.Error);
            return false;
        }
    }
}

internal sealed class TaskManager
{
    private readonly TaskStorageService _storageService;
    private readonly List<TaskItem> _tasks = [];

    public TaskManager(TaskStorageService storageService)
    {
        _storageService = storageService;
        _tasks = _storageService.LoadTasks();
    }

    public IReadOnlyList<TaskItem> Tasks => _tasks;

    public bool AddTask(string title, string? description, TaskPriority priority, out string errorMessage)
    {
        using var traceOperation = AppTracing.BeginOperation("AddTask");
        var stopwatch = Stopwatch.StartNew();
        Log.Debug("Начало операции AddTask");

        try
        {
            if (string.IsNullOrWhiteSpace(title))
            {
                stopwatch.Stop();
                errorMessage = "Название задачи не может быть пустым.";
                Log.Warning("Пользователь ввёл пустое название.");
                Log.Debug("Конец AddTask. Результат: неудача. Время: {Time} ms", stopwatch.ElapsedMilliseconds);
                return false;
            }

            _tasks.Add(new TaskItem
            {
                Title = title.Trim(),
                Description = (description ?? string.Empty).Trim(),
                Priority = priority
            });
            stopwatch.Stop();
            errorMessage = string.Empty;
            Log.Information("Добавлена задача {TaskTitle}", title.Trim());
            Log.Information("Добавлена задача {@Task}", _tasks[^1]);
            Log.Information("Количество задач: {Count}", _tasks.Count);
            Log.Debug("Конец AddTask. Результат: успех. Время: {Time} ms", stopwatch.ElapsedMilliseconds);
            return true;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            errorMessage = "Не удалось добавить задачу из-за внутренней ошибки.";
            AppTracing.TraceError(ex, "AddTask");
            ExceptionHandler.Handle(ex, "AddTask", LogEventLevel.Error);
            Log.Debug("Конец AddTask. Результат: исключение. Время: {Time} ms", stopwatch.ElapsedMilliseconds);
            return false;
        }
    }

    public bool RemoveTask(Guid taskId, out string errorMessage)
    {
        using var traceOperation = AppTracing.BeginOperation("RemoveTask");
        var stopwatch = Stopwatch.StartNew();
        Log.Debug("Начало операции RemoveTask");

        try
        {
            var task = _tasks.FirstOrDefault(t => t.Id == taskId);
            if (task is null)
            {
                stopwatch.Stop();
                errorMessage = "Задача не найдена.";
                Log.Error("Задача с id {TaskId} не найдена.", taskId);
                Log.Debug("Конец RemoveTask. Результат: ошибка. Время: {Time} ms", stopwatch.ElapsedMilliseconds);
                return false;
            }

            _tasks.Remove(task);
            stopwatch.Stop();
            errorMessage = string.Empty;
            Log.Information("Задача \"{Title}\" удалена.", task.Title);
            Log.Information("Количество задач: {Count}", _tasks.Count);
            Log.Debug("Конец RemoveTask. Результат: успех. Время: {Time} ms", stopwatch.ElapsedMilliseconds);
            return true;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            errorMessage = "Не удалось удалить задачу из-за внутренней ошибки.";
            AppTracing.TraceError(ex, "RemoveTask");
            ExceptionHandler.Handle(ex, "RemoveTask", LogEventLevel.Error);
            Log.Debug("Конец RemoveTask. Результат: исключение. Время: {Time} ms", stopwatch.ElapsedMilliseconds);
            return false;
        }
    }

    public bool SetCompleted(Guid taskId, bool isCompleted, out string errorMessage)
    {
        var stopwatch = Stopwatch.StartNew();
        Log.Debug("Начало операции SetCompleted");

        try
        {
            var task = _tasks.FirstOrDefault(t => t.Id == taskId);
            if (task is null)
            {
                stopwatch.Stop();
                errorMessage = "Задача не найдена.";
                Log.Error("Задача с id {TaskId} не найдена.", taskId);
                Log.Debug("Конец SetCompleted. Результат: ошибка. Время: {Time} ms", stopwatch.ElapsedMilliseconds);
                return false;
            }

            task.IsCompleted = isCompleted;
            stopwatch.Stop();
            errorMessage = string.Empty;
            Log.Information("Задача \"{Title}\" помечена как {State}.", task.Title, isCompleted ? "выполненная" : "активная");
            Log.Debug("Конец SetCompleted. Результат: успех. Время: {Time} ms", stopwatch.ElapsedMilliseconds);
            return true;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            errorMessage = "Не удалось обновить задачу из-за внутренней ошибки.";
            ExceptionHandler.Handle(ex, "SetCompleted", LogEventLevel.Error);
            Log.Debug("Конец SetCompleted. Результат: исключение. Время: {Time} ms", stopwatch.ElapsedMilliseconds);
            return false;
        }
    }

    public bool Save(out string errorMessage) => _storageService.SaveTasks(_tasks, out errorMessage);
}

internal enum TaskFilter
{
    All,
    Active,
    Completed
}

internal sealed class MainForm : Form
{
    private readonly TaskManager _taskManager;
    private readonly TextBox _taskInput = new() { PlaceholderText = "Введите название задачи..." };
    private readonly TextBox _taskDescriptionInput = new() { PlaceholderText = "Описание (необязательно)..." };
    private readonly CheckedListBox _tasksList = new() { CheckOnClick = true };
    private readonly ComboBox _filterBox = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ComboBox _priorityBox = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly Label _statusLabel = new() { AutoSize = true, ForeColor = Color.DimGray };
    private readonly List<TaskItem> _visibleTasks = [];
    private bool _isRefreshingList;

    public MainForm(TaskManager taskManager)
    {
        _taskManager = taskManager;

        Text = "Task Manager";
        MinimumSize = new Size(760, 520);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 10F, FontStyle.Regular, GraphicsUnit.Point);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(16),
            ColumnCount = 1,
            RowCount = 3
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var toolbar = BuildToolbar();
        _tasksList.Dock = DockStyle.Fill;
        _tasksList.HorizontalScrollbar = true;
        _tasksList.ItemCheck += TasksListOnItemCheck;

        root.Controls.Add(toolbar, 0, 0);
        root.Controls.Add(_tasksList, 0, 1);
        root.Controls.Add(_statusLabel, 0, 2);
        Controls.Add(root);

        UpdateTasksList();
        UpdateStatus("Готово к работе.");
    }

    private Control BuildToolbar()
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 7
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        _taskInput.Dock = DockStyle.Fill;
        _taskInput.Margin = new Padding(0, 0, 8, 8);
        _taskInput.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.SuppressKeyPress = true;
                AddTask();
            }
        };
        _taskDescriptionInput.Dock = DockStyle.Fill;
        _taskDescriptionInput.Margin = new Padding(0, 0, 8, 8);

        var addButton = CreateButton("Добавить", (_, _) => AddTask());
        var removeButton = CreateButton("Удалить выбранную", (_, _) => RemoveSelectedTask());
        var clearButton = CreateButton("Очистить поля", (_, _) =>
        {
            _taskInput.Clear();
            _taskDescriptionInput.Clear();
        });
        ConfigureFilterBox();
        ConfigurePriorityBox();

        panel.Controls.Add(_taskInput, 0, 0);
        panel.Controls.Add(_taskDescriptionInput, 1, 0);
        panel.Controls.Add(_priorityBox, 2, 0);
        panel.Controls.Add(addButton, 3, 0);
        panel.Controls.Add(removeButton, 4, 0);
        panel.Controls.Add(clearButton, 5, 0);
        panel.Controls.Add(_filterBox, 6, 0);

        return panel;
    }

    private static Button CreateButton(string text, EventHandler onClick)
    {
        var button = new Button
        {
            Text = text,
            AutoSize = true,
            Margin = new Padding(0, 0, 8, 8),
            Padding = new Padding(12, 6, 12, 6)
        };
        button.Click += onClick;
        return button;
    }

    private void AddTask()
    {
        try
        {
            var title = _taskInput.Text;
            var description = _taskDescriptionInput.Text;
            var priority = _priorityBox.SelectedItem as TaskPriority? ?? TaskPriority.Medium;

            if (!_taskManager.AddTask(title, description, priority, out var errorMessage))
            {
                MessageBox.Show(this, errorMessage, "Ошибка", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                UpdateStatus(errorMessage);
                return;
            }

            // Если выбран Completed, новая активная задача будет скрыта.
            // Переключаем фильтр, чтобы пользователь сразу увидел добавленную задачу.
            if ((_filterBox.SelectedItem as TaskFilter?) == TaskFilter.Completed)
            {
                _filterBox.SelectedItem = TaskFilter.All;
            }

            _taskInput.Clear();
            _taskDescriptionInput.Clear();
            _priorityBox.SelectedItem = TaskPriority.Medium;
            UpdateTasksList();
            UpdateStatus($"Добавлена задача: {title.Trim()}");
            SaveTasksSilently();
        }
        catch (Exception ex)
        {
            ExceptionHandler.Handle(ex, "UI.AddTask", LogEventLevel.Error);
            MessageBox.Show(this, "Внутренняя ошибка при добавлении задачи.", "Ошибка", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void RemoveSelectedTask()
    {
        try
        {
            if (_tasksList.SelectedIndex < 0 || _tasksList.SelectedIndex >= _visibleTasks.Count)
            {
                const string message = "Выберите задачу в списке.";
                MessageBox.Show(this, message, "Внимание", MessageBoxButtons.OK, MessageBoxIcon.Information);
                UpdateStatus(message);
                return;
            }

            var selectedTask = _visibleTasks[_tasksList.SelectedIndex];
            if (!_taskManager.RemoveTask(selectedTask.Id, out var errorMessage))
            {
                MessageBox.Show(this, errorMessage, "Ошибка", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                UpdateStatus(errorMessage);
                return;
            }

            UpdateTasksList();
            UpdateStatus($"Удалена задача: {selectedTask.Title}");
            SaveTasksSilently();
        }
        catch (Exception ex)
        {
            ExceptionHandler.Handle(ex, "UI.RemoveSelectedTask", LogEventLevel.Error);
            MessageBox.Show(this, "Внутренняя ошибка при удалении задачи.", "Ошибка", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void UpdateTasksList()
    {
        using var traceOperation = AppTracing.BeginOperation("ShowTasks");
        try
        {
            _isRefreshingList = true;
            _tasksList.BeginUpdate();
            _tasksList.Items.Clear();
            _visibleTasks.Clear();

            foreach (var task in GetFilteredTasks())
            {
                _visibleTasks.Add(task);
                _tasksList.Items.Add(FormatTaskDisplay(task), task.IsCompleted);
            }

            Log.Information("Показан список из {Count} задач", _visibleTasks.Count);
            AppTracing.TraceInformation($"Показан список задач, count={_visibleTasks.Count}");
        }
        catch (Exception ex)
        {
            AppTracing.TraceError(ex, "ShowTasks");
            ExceptionHandler.Handle(ex, "UI.UpdateTasksList", LogEventLevel.Error);
            MessageBox.Show(this, "Внутренняя ошибка при обновлении списка задач.", "Ошибка", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _tasksList.EndUpdate();
            _isRefreshingList = false;
        }
    }

    private IEnumerable<TaskItem> GetFilteredTasks()
    {
        var selectedFilter = _filterBox.SelectedItem as TaskFilter? ?? TaskFilter.All;
        return selectedFilter switch
        {
            TaskFilter.Active => _taskManager.Tasks.Where(t => !t.IsCompleted),
            TaskFilter.Completed => _taskManager.Tasks.Where(t => t.IsCompleted),
            _ => _taskManager.Tasks
        };
    }

    private void ConfigureFilterBox()
    {
        _filterBox.Items.Add(TaskFilter.All);
        _filterBox.Items.Add(TaskFilter.Active);
        _filterBox.Items.Add(TaskFilter.Completed);
        _filterBox.FormattingEnabled = true;
        _filterBox.SelectedIndex = 0;
        _filterBox.Width = 150;
        _filterBox.Margin = new Padding(0, 0, 0, 8);
        _filterBox.Format += (_, args) =>
        {
            args.Value = args.Value switch
            {
                TaskFilter.All => "Все задачи",
                TaskFilter.Active => "Только активные",
                TaskFilter.Completed => "Только выполненные",
                _ => args.Value
            };
        };
        _filterBox.SelectedIndexChanged += (_, _) => UpdateTasksList();
    }

    private void ConfigurePriorityBox()
    {
        _priorityBox.Items.Add(TaskPriority.Low);
        _priorityBox.Items.Add(TaskPriority.Medium);
        _priorityBox.Items.Add(TaskPriority.High);
        _priorityBox.FormattingEnabled = true;
        _priorityBox.SelectedItem = TaskPriority.Medium;
        _priorityBox.Width = 130;
        _priorityBox.Margin = new Padding(0, 0, 8, 8);
        _priorityBox.Format += (_, args) =>
        {
            args.Value = args.Value switch
            {
                TaskPriority.Low => "Приоритет: Низкий",
                TaskPriority.Medium => "Приоритет: Средний",
                TaskPriority.High => "Приоритет: Высокий",
                _ => args.Value
            };
        };
    }

    private static string FormatTaskDisplay(TaskItem task)
    {
        var descriptionPart = string.IsNullOrWhiteSpace(task.Description) ? string.Empty : $" | {task.Description}";
        return $"[{task.Priority}] {task.Title}{descriptionPart}";
    }

    private void TasksListOnItemCheck(object? sender, ItemCheckEventArgs e)
    {
        try
        {
            if (_isRefreshingList)
            {
                return;
            }

            if (e.Index < 0 || e.Index >= _visibleTasks.Count)
            {
                return;
            }

            var task = _visibleTasks[e.Index];
            var shouldBeCompleted = e.NewValue == CheckState.Checked;
            if (!_taskManager.SetCompleted(task.Id, shouldBeCompleted, out var errorMessage))
            {
                MessageBox.Show(this, errorMessage, "Ошибка", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                UpdateStatus(errorMessage);
                return;
            }

            UpdateStatus($"Задача \"{task.Title}\" обновлена.");
            SaveTasksSilently();
        }
        catch (Exception ex)
        {
            ExceptionHandler.Handle(ex, "UI.TasksListOnItemCheck", LogEventLevel.Error);
            MessageBox.Show(this, "Внутренняя ошибка при обновлении статуса задачи.", "Ошибка", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void UpdateStatus(string text)
    {
        var total = _taskManager.Tasks.Count;
        var completed = _taskManager.Tasks.Count(t => t.IsCompleted);
        var active = total - completed;
        _statusLabel.Text = $"Статус: {text} | Всего: {total}, активных: {active}, выполненных: {completed}";
    }

    private void SaveTasksSilently()
    {
        try
        {
            if (_taskManager.Save(out var errorMessage))
            {
                return;
            }

            MessageBox.Show(this, errorMessage, "Ошибка сохранения", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        catch (Exception ex)
        {
            ExceptionHandler.Handle(ex, "UI.SaveTasksSilently", LogEventLevel.Error);
            MessageBox.Show(this, "Критическая ошибка при сохранении задач.", "Ошибка", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}

internal static class Program
{
    private const string DemoModeVariableName = "TASKMANAGER_DEMO_EXCEPTION";

    [STAThread]
    private static void Main()
    {
        ConfigureLogging();
        AppTracing.Initialize();
        RegisterGlobalExceptionHandlers();
        ExceptionHandler.ShouldUseExternalAlerts = true;

        try
        {
            Log.Information("Приложение запущено.");
            ThrowDemoExceptionIfEnabled();
            ApplicationConfiguration.Initialize();
            var storagePath = Path.Combine(AppContext.BaseDirectory, "data", "tasks.json");
            var taskManager = new TaskManager(new TaskStorageService(storagePath));
            Application.Run(new MainForm(taskManager));
            Log.Information("Программа завершается");
        }
        catch (Exception ex)
        {
            AppTracing.TraceError(ex, "Program.Main");
            ExceptionHandler.Handle(ex, "Program.Main", LogEventLevel.Fatal);
            MessageBox.Show(
                $"Произошла критическая ошибка:\n{ex.Message}",
                "Task Manager",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
        finally
        {
            AppTracing.EndSession();
            Log.CloseAndFlush();
        }
    }

    private static void ConfigureLogging()
    {
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
            .WriteTo.File(
                path: "logs\\taskmanager-.log",
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7,
                outputTemplate: "[{Timestamp:yyyy-MM-dd HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
            .WriteTo.File(
                new JsonFormatter(),
                path: "logs\\taskmanager-.json",
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7)
            .CreateLogger();
    }

    private static void ThrowDemoExceptionIfEnabled()
    {
        var demoModeValue = Environment.GetEnvironmentVariable(DemoModeVariableName);
        if (!string.Equals(demoModeValue, "1", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        throw new InvalidOperationException(
            "Демо-исключение: проверка централизованной обработки ошибок и оповещений.");
    }

    private static void RegisterGlobalExceptionHandlers()
    {
        Application.ThreadException += (_, e) =>
        {
            ExceptionHandler.Handle(e.Exception, "Application.ThreadException", LogEventLevel.Fatal);
        };

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex)
            {
                ExceptionHandler.Handle(ex, "AppDomain.UnhandledException", LogEventLevel.Fatal);
                return;
            }

            var unknownException = new Exception("Unhandled non-exception object.");
            ExceptionHandler.Handle(unknownException, "AppDomain.UnhandledException", LogEventLevel.Fatal);
        };
    }
}