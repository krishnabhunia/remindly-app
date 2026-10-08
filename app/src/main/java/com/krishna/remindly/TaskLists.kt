package com.krishna.remindly

import java.util.Locale

/** Task lists are data, separate from Buy lists even when both use the same name. */
data class TaskList(
    val id: Long,
    val name: String = "",
    val icon: String? = null,
    val pinned: Boolean = false,
    val createdAt: Long = 0L,
    val updatedAt: Long = 0L,
    val deletedAt: Long? = null
)

private fun taskListKey(name: String) = name.trim().lowercase(Locale.ROOT)
fun validTaskListName(name: String): Boolean = name.isNotBlank() && !name.trim().equals("Unsorted", ignoreCase = true)

/** Existing Unsorted reminders stay editable; creating or copying a task requires a list. */
fun taskEditorListAllowed(
    settings: AppSettings, tab: Tab, selectedListId: Long?, existing: Item?, duplicate: Boolean = false
): Boolean = tab != Tab.TASKS || !taskListsFirst(settings) || (existing != null && !duplicate) ||
    liveTaskLists(settings.taskLists).any { it.id == selectedListId }

@Suppress("USELESS_ELVIS")
fun healTaskList(list: TaskList): TaskList = list.copy(
    name = (list.name ?: "").trim(),
    icon = list.icon?.trim()?.takeIf { it.isNotBlank() },
    createdAt = list.createdAt.coerceAtLeast(0L),
    updatedAt = maxOf(list.updatedAt, list.deletedAt ?: 0L, 0L)
)

fun liveTaskLists(lists: List<TaskList>): List<TaskList> = lists.filter {
    it.id > 0L && it.deletedAt == null && validTaskListName(it.name)
}

fun taskListNamed(lists: List<TaskList>, name: String?): TaskList? {
    val key = name?.let(::taskListKey)?.takeIf { it.isNotEmpty() } ?: return null
    return liveTaskLists(lists).firstOrNull { taskListKey(it.name) == key }
}

/** A stamped task never falls back to an old name when its list was removed. */
fun taskListIdOf(item: Item, lists: List<TaskList>): Long? {
    if (item.tab != Tab.TASKS) return null
    item.listId?.let { id -> return liveTaskLists(lists).firstOrNull { it.id == id }?.id }
    return taskListNamed(lists, item.group)?.id
}

fun tasksInList(items: List<Item>, listId: Long, lists: List<TaskList>): List<Item> =
    items.filter { it.tab == Tab.TASKS && it.deletedAt == null }.filter {
        if (listId == UNSORTED_LIST_ID) taskListIdOf(it, lists) == null else taskListIdOf(it, lists) == listId
    }

/** The Task prefix differs from Buy's 0x7E and lies outside device-generated IDs. */
fun seedTaskListId(name: String): Long {
    var hash = -0x340d631b7bdddcdbL
    for (byte in taskListKey(name).toByteArray(Charsets.UTF_8)) {
        hash = (hash xor (byte.toLong() and 0xFF)) * 0x100000001b3L
    }
    return (0x7DL shl 56) or (hash and ((1L shl 56) - 1))
}

data class TaskListSeed(val lists: List<TaskList>, val items: List<Item>)

/**
 * Idempotent legacy migration. Configured groups (including empty ones) become lists. A
 * tombstone blocks stale groups from recreating a deleted list. Only changed tasks return,
 * with reminder and completion fields preserved and no synthetic item update timestamp.
 */
fun seedTaskLists(
    items: List<Item>, lists: List<TaskList>, groupNames: List<String>, icons: Map<String, String>, now: Long
): TaskListSeed {
    val out = mergeTaskLists(emptyList(), lists).toMutableList()
    val names = LinkedHashMap<String, String>()
    (groupNames + items.filter { it.tab == Tab.TASKS && it.deletedAt == null && it.listId == null }.mapNotNull { it.group })
        .forEach { raw -> if (validTaskListName(raw)) names.putIfAbsent(taskListKey(raw), raw.trim()) }
    names.forEach { (key, name) ->
        if (taskListNamed(out, name) != null || out.any { it.deletedAt != null && taskListKey(it.name) == key }) return@forEach
        val id = seedTaskListId(name)
        if (out.any { it.id == id }) return@forEach
        out += TaskList(id, name, icons.entries.firstOrNull { taskListKey(it.key) == key }?.value?.trim()?.takeIf { it.isNotBlank() },
            createdAt = now, updatedAt = now)
    }
    // Two devices may independently create the same live name. Keep a stable winner, and
    // remap the duplicate's tasks before tombstoning the duplicate record.
    val remap = mutableMapOf<Long, TaskList>()
    liveTaskLists(out).groupBy { taskListKey(it.name) }.values.filter { it.size > 1 }.forEach { same ->
        val winner = same.sortedWith(compareBy<TaskList>({ it.createdAt }, { it.id })).first()
        same.filter { it.id != winner.id }.forEach { remap[it.id] = winner }
    }
    val healed = out.map { if (it.id in remap) it.copy(deletedAt = now, updatedAt = maxOf(now, it.updatedAt)) else it }
    val changed = items.mapNotNull { item ->
        if (item.tab != Tab.TASKS || item.deletedAt != null) return@mapNotNull null
        val list = item.listId?.let { remap[it] }
            ?: taskListIdOf(item, healed)?.let { id -> liveTaskLists(healed).first { it.id == id } }
        val result = if (list != null) item.copy(listId = list.id, group = list.name)
            else item.copy(listId = null, group = null)
        result.takeIf { it != item }
    }
    return TaskListSeed(healed, changed)
}

/** Per-ID merge; deletion wins an equal timestamp to avoid resurrecting a removed list. */
fun mergeTaskLists(local: List<TaskList>, incoming: List<TaskList>): List<TaskList> {
    val byId = LinkedHashMap<Long, TaskList>()
    (local + incoming).forEach { raw ->
        val list = healTaskList(raw)
        if (list.id <= 0L) return@forEach
        val previous = byId[list.id]
        if (previous == null || compareTaskListVersions(list, previous) > 0) byId[list.id] = list
    }
    return byId.values.toList()
}

private fun compareTaskListVersions(a: TaskList, b: TaskList): Int = compareValuesBy(a, b,
    { it.updatedAt }, { it.deletedAt != null }, { it.deletedAt ?: 0L },
    { it.name }, { it.icon ?: "" }, { it.pinned }, { it.createdAt })

/** Legacy group screens read task names; list records remain the source of truth. */
fun mirrorTaskListsIntoSettings(settings: AppSettings): AppSettings {
    val live = liveTaskLists(settings.taskLists).sortedBy { taskListKey(it.name) }
    return settings.copy(tasksGroups = live.map { it.name })
}

data class TaskListStats(val active: Int, val done: Int, val lastActivity: Long)

fun taskListStats(listItems: List<Item>, listUpdatedAt: Long = 0L): TaskListStats {
    val live = listItems.filter { it.tab == Tab.TASKS && it.deletedAt == null }
    val lastActivity = maxOf(listUpdatedAt, live.maxOfOrNull { maxOf(it.updatedAt, it.createdAt, it.doneAt ?: 0L) } ?: 0L)
    return TaskListStats(live.count { !it.done }, live.count { it.done }, lastActivity)
}

fun sortedTaskLists(
    lists: List<TaskList>, mode: String, lastActivity: (TaskList) -> Long = { it.updatedAt }
): List<TaskList> {
    val order = if (mode == "AZ") compareBy<TaskList> { taskListKey(it.name) }
        else compareByDescending<TaskList> { lastActivity(it) }.thenBy { taskListKey(it.name) }
    return liveTaskLists(lists).sortedWith(compareBy<TaskList> { !it.pinned }.then(order).thenBy { it.id })
}
