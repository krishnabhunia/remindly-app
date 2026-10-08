package com.krishna.remindly

import android.content.Context

/** Writes use the existing settings/item flows, dirty flag and per-record sync timestamps. */
object TaskListStore {
    fun all(): List<TaskList> = SettingsStore.s.value.taskLists
    fun live(): List<TaskList> = liveTaskLists(all())
    fun get(id: Long?): TaskList? = id?.let { key -> live().firstOrNull { it.id == key } }
    fun itemsOf(listId: Long): List<Item> = tasksInList(ItemStore.items.value, listId, all())

    private fun writeLists(transform: (List<TaskList>) -> List<TaskList>) {
        SettingsStore.update { settings -> mirrorTaskListsIntoSettings(settings.copy(taskLists = transform(settings.taskLists))) }
    }

    private fun nextStamp(list: TaskList): Long = maxOf(System.currentTimeMillis(), list.updatedAt).let {
        if (it == Long.MAX_VALUE) it else it + 1L
    }

    fun reconcile(context: Context) {
        runCatching {
            val settings = SettingsStore.s.value
            val seed = seedTaskLists(ItemStore.items.value, settings.taskLists, settings.tasksGroups,
                settings.groupIcons, System.currentTimeMillis())
            if (seed.items.isNotEmpty()) {
                val changed = seed.items.associateBy { it.id }
                ItemStore.replaceAll(ItemStore.items.value.map { changed[it.id] ?: it })
            }
            val mirrored = mirrorTaskListsIntoSettings(settings.copy(taskLists = seed.lists))
            if (mirrored != settings) SettingsStore.update { current ->
                mirrorTaskListsIntoSettings(current.copy(taskLists = seed.lists))
            }
        }.onFailure { Logger.e(context, "TASK_LISTS", it, "task list reconcile failed") }
    }

    fun create(context: Context, name: String, icon: String?, pinned: Boolean = false): TaskList? = runCatching {
        if (!validTaskListName(name) || taskListNamed(all(), name) != null) return@runCatching null
        val now = System.currentTimeMillis()
        val list = TaskList(Ids.next(), name.trim(), icon?.trim()?.takeIf { it.isNotBlank() }, pinned, now, now)
        writeLists { it + list }
        list
    }.onFailure { Logger.e(context, "TASK_LISTS", it, "create task list failed") }.getOrNull()

    fun edit(context: Context, list: TaskList, name: String, icon: String?, pinned: Boolean = list.pinned): Boolean = runCatching {
        val current = get(list.id) ?: return@runCatching false
        if (!validTaskListName(name) || taskListNamed(all().filter { it.id != list.id }, name) != null) return@runCatching false
        val tasks = itemsOf(list.id)
        val edited = current.copy(name = name.trim(), icon = icon?.trim()?.takeIf { it.isNotBlank() },
            pinned = pinned, updatedAt = nextStamp(current))
        writeLists { lists -> lists.map { if (it.id == edited.id) edited else it } }
        if (current.name != edited.name) ItemStore.upsertAll(tasks.map { it.copy(group = edited.name, listId = edited.id) })
        true
    }.onFailure { Logger.e(context, "TASK_LISTS", it, "edit task list failed") }.getOrDefault(false)

    fun togglePin(context: Context, list: TaskList): Boolean = runCatching {
        val current = get(list.id) ?: return@runCatching false
        writeLists { lists -> lists.map {
            if (it.id == current.id) it.copy(pinned = !current.pinned, updatedAt = nextStamp(current)) else it
        } }
        true
    }.onFailure { Logger.e(context, "TASK_LISTS", it, "pin task list failed") }.getOrDefault(false)

    fun moveItem(context: Context, item: Item, list: TaskList?): Boolean = runCatching {
        if (item.tab != Tab.TASKS) return@runCatching false
        val target = list?.let { get(it.id) ?: return@runCatching false }
        if (target == null && ItemStore.get(item.id) == null) return@runCatching false
        Engine.addOrUpdate(context, item.copy(listId = target?.id, group = target?.name))
        true
    }.onFailure { Logger.e(context, "TASK_LISTS", it, "move task between lists failed") }.getOrDefault(false)

    /** The list is tombstoned; its tasks keep all reminder/completion fields in Unsorted. */
    fun delete(context: Context, list: TaskList): Boolean = runCatching {
        val current = get(list.id) ?: return@runCatching false
        val tasks = itemsOf(current.id)
        val now = nextStamp(current)
        writeLists { lists -> lists.map { if (it.id == current.id) it.copy(deletedAt = now, updatedAt = now) else it } }
        ItemStore.upsertAll(tasks.map { it.copy(listId = null, group = null) })
        true
    }.onFailure { Logger.e(context, "TASK_LISTS", it, "delete task list failed") }.getOrDefault(false)
}
