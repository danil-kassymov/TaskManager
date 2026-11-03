using TaskManager.Models;

namespace TaskManager.ViewModels;

/// <summary>First-run content: one project, common labels and a short guide issue.</summary>
static class Seed
{
    public static void EnsureInitialData(AppServices s)
    {
        if (s.Projects.All().Count > 0 || s.Issues.Count() > 0) return;

        var project = s.Projects.Create("Мой проект", "TM", "#5E6AD2");
        var bug = s.Labels.Create("Bug", "#EB5757");
        s.Labels.Create("Feature", "#BB87FC");
        s.Labels.Create("Improvement", "#26B5CE");

        var guide = s.Issues.Create(project.Id, "Добро пожаловать — прочитай меня", IssueStatus.Todo, Priority.High, description: Guide);
        s.Issues.Create(project.Id, "Отметь эту подзадачу выполненной", parentId: guide.Id);
        s.Issues.Create(project.Id, "Создай свою первую задачу клавишей C", parentId: guide.Id);
        var sample = s.Issues.Create(project.Id, "Пример бага: падение при пустом вводе", IssueStatus.Backlog, Priority.Medium,
            description: "Шаги:\n1. Открыть форму\n2. Нажать «Сохранить» с пустым полем\n\nОжидалось: сообщение об ошибке.");
        s.Issues.SetLabels(sample.Id, [bug.Id]);
    }

    const string Guide = """
        Это локальный трекер задач. Всё хранится на этом компьютере и работает без интернета.

        ## Горячие клавиши
        - `C` — новая задача, `Ctrl+K` — командная палитра, `/` — фильтр
        - `J` / `K` или стрелки — по списку, `Esc` — закрыть
        - `S` — статус, `P` — приоритет, `L` — метки, `1`–`4` — приоритет, `0` — без приоритета
        - `Ctrl+B` — список ↔ канбан, `Delete` — удалить задачу

        ## Описание в Markdown
        Двойной клик по описанию — редактирование, `Esc` или клик вне — просмотр.
        - **жирный** (`Ctrl+B`), *курсив* (`Ctrl+I`), `код`, ссылки (`Ctrl+K`)
        - Вставь скриншот через `Ctrl+V` или перетащи картинку в редактор
        - Выдели текст и вставь URL — получится ссылка
        - Ссылки на задачи вида TM-1 кликабельны

        ## Чек-лист
        - [x] Открыть приложение
        - [ ] Кликнуть по этому чекбоксу
        """;
}
