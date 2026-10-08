package com.krishna.remindly

import android.content.Context
import androidx.test.core.app.ApplicationProvider
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.annotation.Config
import java.io.File

/** User-visible list membership and upgrade behavior, independent of Android UI. */
class TaskListsTest {
    private val now = 1_790_000_000_000L
    private fun task(id: Long, group: String? = null, listId: Long? = null) =
        Item(id = id, tab = Tab.TASKS, title = "Task $id", group = group, listId = listId,
            createdAt = now - 100, updatedAt = now - 50)
    private fun list(id: Long, name: String, updated: Long = now - 100) =
        TaskList(id = id, name = name, createdAt = now - 100, updatedAt = updated)

    @Test fun taskInsideAListAppearsThere_andIdWinsOverOldName() {
        val lists = listOf(list(10, "Home"), list(20, "Work"))
        val saved = task(1, group = "Home", listId = 20)
        assertEquals(20L, taskListIdOf(saved, lists))
        assertEquals(listOf(saved), tasksInList(listOf(saved), 20, lists))
        assertTrue(tasksInList(listOf(saved), 10, lists).isEmpty())
        assertEquals(10L, taskListIdOf(task(2, group = " home "), lists))
    }

    @Test fun aDeletedOrMissingListPlacesTaskInUnsorted_evenWithAStaleName() {
        val lists = listOf(list(10, "Home"), list(20, "Work").copy(deletedAt = now))
        val items = listOf(task(1), task(2, group = "Home", listId = 999),
            task(3, group = "Work", listId = 20), task(4, group = "Unknown"))
        assertEquals(listOf(1L, 2L, 3L, 4L),
            tasksInList(items, UNSORTED_LIST_ID, lists).map { it.id })
        assertTrue(tasksInList(items, 10, lists).isEmpty())
    }

    @Test fun shopAndLearnItemsNeverEnterTaskLists_evenWithTheSameListIdAndName() {
        val taskList = list(10, "Home")
        val shopList = ShopList(id = 10, name = "Home")
        val t = task(1, "Home", 10)
        val buy = t.copy(id = 2, tab = Tab.SHOP)
        val learn = t.copy(id = 3, tab = Tab.LEARN)
        val deletedTask = t.copy(id = 4, deletedAt = now)
        assertEquals(listOf(t), tasksInList(listOf(t, buy, learn, deletedTask), 10, listOf(taskList)))
        assertNull(taskListIdOf(buy, listOf(taskList)))
        assertNull(taskListIdOf(learn, listOf(taskList)))
        assertEquals(listOf(buy), itemsInList(listOf(t, buy, learn), 10, listOf(shopList)))
    }

    @Test fun upgradeCreatesConfiguredEmptyLists_andCombinesCaseVariants() {
        val items = listOf(task(1, " Work "), task(2, "work"), task(3, "Errands"), task(4),
            task(5, "Shop only").copy(tab = Tab.SHOP), task(6, "Deleted only").copy(deletedAt = now))
        val seed = seedTaskLists(items, emptyList(), listOf("Work", " Personal ", "WORK", ""),
            mapOf("Work" to "💼", "Personal" to "🌿"), now)
        assertEquals(setOf("Work", "Personal", "Errands"), liveTaskLists(seed.lists).map { it.name }.toSet())
        val work = taskListNamed(seed.lists, " WORK ")!!
        assertEquals("💼", work.icon)
        assertEquals("🌿", taskListNamed(seed.lists, "personal")!!.icon)
        assertEquals(work.id, seed.items.first { it.id == 1L }.listId)
        assertEquals(work.id, seed.items.first { it.id == 2L }.listId)
        assertEquals("Work", seed.items.first { it.id == 1L }.group)
        assertEquals(setOf(1L, 2L, 3L), seed.items.map { it.id }.toSet())
        assertTrue(tasksInList(items, taskListNamed(seed.lists, "Personal")!!.id, seed.lists).isEmpty())
    }

    @Test fun migrationPreservesTaskState_andSecondRunChangesNothing() {
        val original = task(1, "Work").copy(notes = "Keep my note", priority = Priority.URGENT,
            dueAt = now + 1000, snoozedUntil = now + 2000, alertType = "RN", personal = true,
            repeatMode = "WEEKLY", repeatDays = listOf(1, 5), repeatDone = 2,
            missedAt = listOf(now - 1000), done = true, doneAt = now - 10, calEventId = 42)
        val first = seedTaskLists(listOf(original), emptyList(), emptyList(), emptyMap(), now)
        val applied = first.items.single()
        assertEquals(original.copy(listId = first.lists.single().id), applied)
        val second = seedTaskLists(listOf(applied), first.lists, listOf("Work"), emptyMap(), now + 1000)
        assertEquals(first.lists, second.lists)
        assertTrue(second.items.isEmpty())
    }

    @Test fun twoDevicesUpgradeTheSameNameToTheSameStableTaskListId() {
        val a = seedTaskLists(listOf(task(1, " Work ")), emptyList(), emptyList(), emptyMap(), now)
        val b = seedTaskLists(listOf(task(2, "work")), emptyList(), emptyList(), emptyMap(), now + 1000)
        assertEquals(a.lists.single().id, b.lists.single().id)
        assertEquals(1, liveTaskLists(mergeTaskLists(a.lists, b.lists)).size)
        assertEquals(seedTaskListId("Work"), seedTaskListId(" work "))
        assertNotEquals(seedTaskListId("Work"), seedListId("Work"))
        assertNotEquals(seedTaskListId("Work"), seedTaskListId("Works"))
    }

    @Test fun deletedListDoesNotResurrectFromOlderGroupOrItemData() {
        val dead = list(10, "Work").copy(deletedAt = now, updatedAt = now)
        val seed = seedTaskLists(listOf(task(1, " work ", 10), task(2, "Work")),
            listOf(dead), listOf("WORK"), emptyMap(), now + 1000)
        assertTrue(liveTaskLists(seed.lists).isEmpty())
        val changed = seed.items.associateBy { it.id }
        val applied = listOf(task(1, " work ", 10), task(2, "Work")).map { changed[it.id] ?: it }
        assertEquals(listOf(1L, 2L), tasksInList(applied, UNSORTED_LIST_ID, seed.lists).map { it.id })
        val again = seedTaskLists(applied, seed.lists, listOf("Work"), emptyMap(), now + 2000)
        assertTrue(liveTaskLists(again.lists).isEmpty())
    }

    @Test fun duplicateNamesCreatedOnTwoDevicesConverge_andTasksFollowTheSurvivingList() {
        val older = list(10, "Work").copy(createdAt = now - 1000)
        val newer = list(20, "work").copy(createdAt = now)
        val original = task(1, "work", 20).copy(done = true, doneAt = now - 1, dueAt = now + 1000)
        val seed = seedTaskLists(listOf(original), listOf(newer, older), emptyList(), emptyMap(), now + 1)
        assertEquals(listOf(10L), liveTaskLists(seed.lists).map { it.id })
        assertEquals(original.copy(listId = older.id, group = older.name), seed.items.single())
        assertTrue(seed.lists.first { it.id == newer.id }.deletedAt != null)
        val again = seedTaskLists(seed.items, seed.lists, listOf("Work"), emptyMap(), now + 2)
        assertEquals(seed.lists, again.lists)
        assertTrue(again.items.isEmpty())
    }

    @Test fun cardCountsAndRecentSortIgnoreDeletedTasks_andKeepPinnedListsFirst() {
        val home = list(10, "Home", updated = 10)
        val work = list(20, "Work", updated = 20)
        val pinned = list(30, "Personal", updated = 1).copy(pinned = true)
        val items = listOf(task(1, "Home", 10).copy(createdAt = 5, updatedAt = 80),
            task(2, "Home", 10).copy(createdAt = 6, updatedAt = 50, done = true, doneAt = 90),
            task(3, "Home", 10).copy(updatedAt = 999, deletedAt = 999))
        val stats = taskListStats(items, home.updatedAt)
        assertEquals(1, stats.active)
        assertEquals(1, stats.done)
        assertEquals(90L, stats.lastActivity)
        assertEquals(0, taskListStats(emptyList(), work.updatedAt).active)
        assertEquals(20L, taskListStats(emptyList(), work.updatedAt).lastActivity)
        assertEquals(listOf(30L, 10L, 20L), sortedTaskLists(listOf(work, home, pinned), "RECENT") {
            if (it.id == 10L) stats.lastActivity else it.updatedAt
        }.map { it.id })
        assertEquals(listOf(30L, 10L, 20L), sortedTaskLists(listOf(work, home, pinned), "AZ").map { it.id })
    }

    @Test fun perListSyncMergeRetainsEmptyLists_andNewerDeletesWinOverOldCopies() {
        val old = list(10, "Work", updated = 10)
        val deleted = old.copy(updatedAt = 30, deletedAt = 30)
        val empty = list(20, "Empty", updated = 20)
        val merged = mergeTaskLists(listOf(deleted), listOf(old, empty))
        assertEquals(listOf(20L), liveTaskLists(merged).map { it.id })
        assertEquals(deleted, merged.first { it.id == 10L })
        assertEquals(merged.toSet(), mergeTaskLists(listOf(old, empty), listOf(deleted)).toSet())
        assertEquals(merged.toSet(), mergeTaskLists(merged, merged).toSet())
    }

    @Test fun deletionWinsAnEqualSyncTimestampRegardlessOfMergeOrder() {
        val alive = list(10, "Work", updated = 30)
        val deleted = alive.copy(deletedAt = 30)
        assertEquals(deleted, mergeTaskLists(listOf(alive), listOf(deleted)).single())
        assertEquals(deleted, mergeTaskLists(listOf(deleted), listOf(alive)).single())
    }

    @Test fun taskSettingsMirrorNeverAltersTheShopRegistryOrItsGroups() {
        val shop = ShopList(id = 10, name = "Home", icon = "🛒")
        val settings = AppSettings(taskLists = listOf(list(10, "Home").copy(icon = "🏡"),
            list(20, "Gone").copy(deletedAt = now)), shopLists = listOf(shop),
            tasksGroups = listOf("Gone"), shopGroups = listOf("Home"), groupIcons = mapOf("Home" to "🛒"))
        val mirrored = mirrorTaskListsIntoSettings(settings)
        assertEquals(listOf("Home"), mirrored.tasksGroups)
        assertEquals(settings.shopLists, mirrored.shopLists)
        assertEquals(settings.shopGroups, mirrored.shopGroups)
        assertEquals("🛒", mirrored.shopLists.single().icon)
    }

    @Test fun listsFirstUsesGlobalDefaultAndExplicitTaskOverride() {
        assertTrue(taskListsFirst(AppSettings()))
        assertFalse(taskListsFirst(AppSettings(globalTaskListsFirst = false)))
        assertTrue(taskListsFirst(AppSettings(globalTaskListsFirst = false, tasksListsFirst = 1)))
        assertFalse(taskListsFirst(AppSettings(globalTaskListsFirst = true, tasksListsFirst = 0)))
        assertTrue(taskListsFirst(AppSettings(globalTaskListsFirst = true, tasksListsFirst = -1)))
    }

    @Test fun aNewTaskOrDuplicateCannotBeSavedUntilALiveListHasBeenChosen() {
        val work = list(10, "Work")
        val gone = list(20, "Removed").copy(deletedAt = now)
        val settings = AppSettings(taskLists = listOf(work, gone))
        assertFalse(taskEditorListAllowed(settings, Tab.TASKS, null, existing = null))
        assertFalse(taskEditorListAllowed(settings, Tab.TASKS, UNSORTED_LIST_ID, existing = null))
        assertFalse(taskEditorListAllowed(settings, Tab.TASKS, 999, existing = null))
        assertFalse(taskEditorListAllowed(settings, Tab.TASKS, gone.id, existing = null))
        assertTrue(taskEditorListAllowed(settings, Tab.TASKS, work.id, existing = null))
        val oldUnsorted = task(1)
        val oldAssigned = task(2, work.name, work.id)
        assertFalse(taskEditorListAllowed(settings, Tab.TASKS, null, oldUnsorted, duplicate = true))
        assertFalse(taskEditorListAllowed(settings, Tab.TASKS, gone.id, oldAssigned, duplicate = true))
        assertTrue(taskEditorListAllowed(settings, Tab.TASKS, work.id, oldUnsorted, duplicate = true))
    }

    @Test fun anExistingUnsortedTaskCanStillBeEdited_andClassicAndOtherTabsKeepTheirSaveFlow() {
        val settings = AppSettings()
        assertTrue(taskEditorListAllowed(settings, Tab.TASKS, null, task(1)))
        assertTrue(taskEditorListAllowed(settings, Tab.TASKS, null, task(2, "Old group", 999)))
        assertTrue(taskEditorListAllowed(settings.copy(globalTaskListsFirst = false), Tab.TASKS, null, null))
        assertTrue(taskEditorListAllowed(settings.copy(tasksListsFirst = 0), Tab.TASKS, null, null))
        assertFalse(taskEditorListAllowed(settings.copy(globalTaskListsFirst = false, tasksListsFirst = 1),
            Tab.TASKS, null, null))
        assertTrue(taskEditorListAllowed(settings, Tab.SHOP, null, null))
        assertTrue(taskEditorListAllowed(settings, Tab.LEARN, null, null))
    }

    @Test fun preListSettingsRecoverTheOnDefault_andModernOffChoiceSurvives() {
        val legacy = gson.fromJson("""{"ver":43}""", AppSettings::class.java)
        val upgraded = SettingsStore.migrate(healSettings(legacy))
        assertTrue(upgraded.globalTaskListsFirst)
        assertEquals(-1, upgraded.tasksListsFirst)
        assertEquals("RECENT", upgraded.taskListSort)
        assertTrue(upgraded.taskLists.isEmpty())
        assertEquals(AppSettings().ver, upgraded.ver)
        assertFalse(SettingsStore.migrate(upgraded.copy(globalTaskListsFirst = false)).globalTaskListsFirst)
    }

    @Test fun settingsDiscardAndResetPreserveTaskListDataWhileRevertingPreferences() {
        val empty = list(10, "Empty")
        val deleted = list(20, "Removed").copy(deletedAt = now)
        val snapshot = AppSettings()
        val current = snapshot.copy(taskLists = listOf(empty, deleted), globalTaskListsFirst = false,
            tasksListsFirst = 0, taskListSort = "AZ")
        val discarded = keepListData(snapshot, current)
        assertTrue(discarded.globalTaskListsFirst)
        assertEquals(listOf(empty, deleted), discarded.taskLists)
        assertEquals(listOf("Empty"), discarded.tasksGroups)
        val taskReset = resetSettingsFor(current, "TASKS")
        assertEquals(-1, taskReset.tasksListsFirst)
        assertEquals("RECENT", taskReset.taskListSort)
        assertFalse(taskReset.globalTaskListsFirst)
        assertEquals(current.taskLists, taskReset.taskLists)
        val globalReset = resetSettingsFor(current, null)
        assertTrue(globalReset.globalTaskListsFirst)
        assertEquals(current.tasksListsFirst, globalReset.tasksListsFirst)
        assertEquals(current.taskLists, globalReset.taskLists)
    }
}

/** Real local-store, import and sync callback checks; no network or Firebase account needed. */
@RunWith(RobolectricTestRunner::class)
@Config(sdk = [28])
class TaskListStoreTest {
    private lateinit var context: Context
    private val now = 1_790_000_000_000L

    @Before fun freshStores() {
        context = ApplicationProvider.getApplicationContext()
        listOf("items.json", "settings.json", "places.json", "calls.json").forEach {
            File(context.filesDir, it).delete()
        }
        Stores.init(context)
        SettingsStore.replace(AppSettings())
        ItemStore.replaceAll(emptyList())
        PlaceStore.replaceAll(emptyList())
        CallStore.replaceAll(emptyList())
    }

    private fun task(id: Long, list: TaskList, done: Boolean = false) = Item(
        id = id, tab = Tab.TASKS, title = "Task $id", group = list.name, listId = list.id,
        notes = "Keep my note", createdAt = now - 100, updatedAt = now - 50,
        dueAt = now + 1000, snoozedUntil = now + 2000, priority = Priority.URGENT,
        alertType = "RN", personal = true, repeatMode = "WEEKLY", repeatDays = listOf(1, 5),
        repeatCount = 8, repeatDone = 2, missedAt = listOf(now - 1000), calEventId = 42,
        done = done, doneAt = if (done) now - 10 else null, returnAt = now + 3000
    )

    private fun assertTaskStatePreserved(before: Item, after: Item) {
        assertEquals(before.copy(listId = after.listId, group = after.group, updatedAt = after.updatedAt), after)
    }

    private fun applyRemoteSettings(remote: AppSettings) {
        // Invoke the actual listener apply path without a Firestore connection. Testing just the
        // merge helper would miss a dropped field at this integration boundary.
        val apply = SyncRepo::class.java.getDeclaredMethod("applySettings", Context::class.java, AppSettings::class.java)
        apply.isAccessible = true
        apply.invoke(SyncRepo, context, remote)
    }

    @Test fun createAnEmptyList_thenReopenKeepsItAvailableBeforeAnyTaskExists() {
        val created = TaskListStore.create(context, " Work ", "💼")!!
        assertEquals("Work", created.name)
        assertTrue(TaskListStore.itemsOf(created.id).isEmpty())
        SettingsStore.load()
        ItemStore.load()
        TaskListStore.reconcile(context)
        assertEquals(created, TaskListStore.get(created.id))
        assertEquals(listOf("Work"), SettingsStore.s.value.tasksGroups)
        assertTrue(ItemStore.items.value.isEmpty())
    }

    @Test fun newListsRejectEmptyReservedAndDuplicateNames() {
        val created = TaskListStore.create(context, "Work", null)!!
        assertNull(TaskListStore.create(context, " work ", "💼"))
        assertNull(TaskListStore.create(context, "   ", null))
        assertNull(TaskListStore.create(context, " UNSORTED ", null))
        assertEquals(listOf(created), TaskListStore.live())
    }

    @Test fun renameAListUpdatesItsTasks_andKeepsTheirReminderAndDoneStateAfterReopen() {
        val work = TaskListStore.create(context, "Work", "💼")!!
        val active = task(1, work)
        val done = task(2, work, done = true)
        ItemStore.replaceAll(listOf(active, done))
        assertTrue(TaskListStore.edit(context, work, " Office ", "🏢", pinned = true))
        SettingsStore.load()
        ItemStore.load()
        TaskListStore.reconcile(context)
        val renamed = TaskListStore.get(work.id)!!
        assertEquals("Office", renamed.name)
        assertEquals("🏢", renamed.icon)
        assertTrue(renamed.pinned)
        assertEquals(setOf(1L, 2L), TaskListStore.itemsOf(work.id).map { it.id }.toSet())
        listOf(active, done).forEach { before ->
            val after = ItemStore.get(before.id)!!
            assertEquals("Office", after.group)
            assertEquals(work.id, after.listId)
            assertTaskStatePreserved(before, after)
        }
    }

    @Test fun moveATaskToAnotherListKeepsReminderRecurrenceAndCompletionState() {
        val work = TaskListStore.create(context, "Work", null)!!
        val home = TaskListStore.create(context, "Home", null)!!
        val original = task(1, work, done = true)
        ItemStore.replaceAll(listOf(original))
        assertTrue(TaskListStore.moveItem(context, original, home))
        ItemStore.load()
        val moved = ItemStore.get(original.id)!!
        assertEquals(home.id, moved.listId)
        assertEquals(home.name, moved.group)
        assertTrue(TaskListStore.itemsOf(work.id).isEmpty())
        assertTaskStatePreserved(original, moved)
        assertTrue(TaskListStore.delete(context, home))
        val unsorted = ItemStore.get(original.id)!!
        assertFalse(TaskListStore.moveItem(context, unsorted, home))
        assertEquals(unsorted, ItemStore.get(original.id))
    }

    @Test fun deleteAListKeepsTasksInUnsorted_andNeverChangesBuyItemsOrResurrectsOnReopen() {
        val work = TaskListStore.create(context, "Work", "💼")!!
        val active = task(1, work)
        val done = task(2, work, done = true)
        val buy = task(3, work).copy(tab = Tab.SHOP, personal = false)
        val shopList = ShopList(id = work.id, name = work.name, icon = "🛒")
        SettingsStore.update { it.copy(shopLists = listOf(shopList), shopGroups = listOf(work.name)) }
        ItemStore.replaceAll(listOf(active, done, buy))
        assertTrue(TaskListStore.delete(context, work))
        assertNull(TaskListStore.get(work.id))
        assertTrue(TaskListStore.all().single().deletedAt != null)
        listOf(active, done).forEach { before ->
            val after = ItemStore.get(before.id)!!
            assertNull(after.listId)
            assertNull(after.group)
            assertTaskStatePreserved(before, after)
        }
        SettingsStore.load()
        ItemStore.load()
        TaskListStore.reconcile(context)
        assertTrue(TaskListStore.live().isEmpty())
        assertTrue(SettingsStore.s.value.tasksGroups.isEmpty())
        assertEquals(setOf(1L, 2L), TaskListStore.itemsOf(UNSORTED_LIST_ID).map { it.id }.toSet())
        assertEquals(listOf(shopList), SettingsStore.s.value.shopLists)
        assertEquals(listOf("Work"), SettingsStore.s.value.shopGroups)
        assertEquals(buy, ItemStore.get(buy.id))
    }

    @Test fun dataBackupRoundTripRetainsAnEmptyListAndTombstoneWithoutResurrection() {
        val empty = TaskListStore.create(context, "Empty", "🌿")!!
        val work = TaskListStore.create(context, "Work", "💼")!!
        val original = task(1, work, done = true)
        ItemStore.replaceAll(listOf(original))
        assertTrue(TaskListStore.delete(context, work))
        val lists = TaskListStore.all()
        val unsorted = ItemStore.get(original.id)!!
        val exported = Backup.exportDataJson()
        val parsed = Backup.parseData(exported)!!
        assertEquals(lists.toSet(), parsed.taskLists!!.toSet())
        SettingsStore.replace(AppSettings())
        ItemStore.replaceAll(emptyList())
        Backup.replaceData(context, parsed)
        SettingsStore.load()
        ItemStore.load()
        TaskListStore.reconcile(context)
        assertEquals(listOf(empty), TaskListStore.live())
        assertEquals(lists.toSet(), TaskListStore.all().toSet())
        assertEquals(unsorted, ItemStore.get(original.id))
        assertEquals(listOf(original.id), TaskListStore.itemsOf(UNSORTED_LIST_ID).map { it.id })
    }

    @Test fun legacyBackupWithoutNewFieldsStillImportsTaskGroupsAndConfiguredEmptyLists() {
        val json = """{"kind":"remindly-data","items":[{"id":1,"tab":"TASKS","title":"Old task","group":" Work "},{"id":2,"tab":"SHOP","title":"Milk","group":"Work"}],"tasksGroups":["Work","Empty"],"shopGroups":["Work"],"calls":[],"places":[]}"""
        val parsed = Backup.parseData(json)!!
        assertTrue(parsed.taskLists!!.isEmpty())
        assertNull(parsed.items.first { it.id == 1L }.listId)
        Backup.replaceData(context, parsed)
        assertEquals(setOf("Work", "Empty"), TaskListStore.live().map { it.name }.toSet())
        val work = taskListNamed(TaskListStore.all(), "Work")!!
        assertEquals(listOf(1L), TaskListStore.itemsOf(work.id).map { it.id })
        assertTrue(TaskListStore.itemsOf(taskListNamed(TaskListStore.all(), "Empty")!!.id).isEmpty())
        assertEquals(listOf("Work"), ShopListStore.live().map { it.name })
        assertNotEquals(work.id, ShopListStore.live().single().id)
        assertEquals("Work", ItemStore.get(2)!!.group)
    }

    @Test fun importingSettingsCannotDropExistingEmptyListsAndKeepsTombstones() {
        val empty = TaskListStore.create(context, "Empty", null)!!
        val work = TaskListStore.create(context, "Work", null)!!
        assertTrue(TaskListStore.delete(context, work))
        val incoming = TaskList(id = 500, name = "Remote empty", createdAt = now, updatedAt = now)
        val payload = Backup.SettingsBlob(settings = AppSettings(theme = "DARK", taskLists = listOf(incoming)))
        val parsed = Backup.parseSettings(gson.toJson(payload))!!
        Backup.applySettings(parsed)
        assertEquals("DARK", SettingsStore.s.value.theme)
        assertEquals(setOf(empty.id, incoming.id), TaskListStore.live().map { it.id }.toSet())
        assertTrue(TaskListStore.all().first { it.id == work.id }.deletedAt != null)
    }

    @Test fun aNewerRemoteSettingsDocumentAddsEmptyLists_andRetainsLocalListDataAndDeviceFlags() {
        val localEmpty = TaskList(id = 100, name = "Local empty", createdAt = 1, updatedAt = 10)
        val removed = TaskList(id = 200, name = "Removed", createdAt = 1, updatedAt = 30, deletedAt = 30)
        val shop = ShopList(id = 100, name = "Local empty", icon = "🛒")
        SettingsStore.replace(AppSettings(taskLists = listOf(localEmpty, removed), shopLists = listOf(shop),
            settingsUpdatedAt = 100, cloudSync = true, lastSyncAt = 88, lastDataBackupAt = 99))
        val remoteEmpty = TaskList(id = 300, name = "Remote empty", createdAt = 1, updatedAt = 20)
        val remote = AppSettings(taskLists = listOf(removed.copy(updatedAt = 10, deletedAt = null), remoteEmpty),
            settingsUpdatedAt = Long.MAX_VALUE, cloudSync = false, lastSyncAt = 0, lastDataBackupAt = 0)
        applyRemoteSettings(remote)
        val applied = SettingsStore.s.value
        assertEquals(setOf(100L, 300L), TaskListStore.live().map { it.id }.toSet())
        assertEquals(removed, TaskListStore.all().first { it.id == removed.id })
        assertEquals(listOf(shop), applied.shopLists)
        assertTrue(applied.cloudSync)
        assertEquals(88L, applied.lastSyncAt)
        assertEquals(99L, applied.lastDataBackupAt)
        val wire = healSettings(gson.fromJson(gson.toJson(Backup.settingsForSync(applied)), AppSettings::class.java))
        assertEquals(applied.taskLists.toSet(), wire.taskLists.toSet())
        assertFalse(wire.cloudSync)
        assertEquals(0L, wire.lastSyncAt)
    }

    @Test fun olderRemotePreferencesStillApplyANewerListDeletion_withoutChangingCurrentTheme() {
        val work = TaskList(id = 100, name = "Work", createdAt = 1, updatedAt = 10)
        SettingsStore.replace(AppSettings(theme = "DARK", taskLists = listOf(work), settingsUpdatedAt = 100))
        val gone = work.copy(updatedAt = 30, deletedAt = 30)
        val empty = TaskList(id = 200, name = "Remote empty", createdAt = 1, updatedAt = 20)
        applyRemoteSettings(AppSettings(theme = "LIGHT", taskLists = listOf(gone, empty), settingsUpdatedAt = 50))
        assertEquals("DARK", SettingsStore.s.value.theme)
        assertEquals(listOf(empty), TaskListStore.live())
        assertEquals(gone, TaskListStore.all().first { it.id == work.id })
        SettingsStore.load()
        TaskListStore.reconcile(context)
        assertEquals(listOf(empty), TaskListStore.live())
    }
}
