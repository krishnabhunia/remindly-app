package com.krishna.remindly

import androidx.compose.foundation.ExperimentalFoundationApi
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.combinedClickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.ExperimentalLayoutApi
import androidx.compose.foundation.layout.FlowRow
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.KeyboardActions
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Add
import androidx.compose.material.icons.filled.ArrowDropDown
import androidx.compose.material.icons.filled.Close
import androidx.compose.material.icons.filled.CheckCircle
import androidx.compose.material.icons.filled.CreateNewFolder
import androidx.compose.material.icons.filled.Delete
import androidx.compose.material.icons.filled.Edit
import androidx.compose.material.icons.filled.FolderOpen
import androidx.compose.material.icons.filled.MoreVert
import androidx.compose.material.icons.filled.PushPin
import androidx.compose.material.icons.filled.Search
import androidx.compose.material.icons.filled.Settings
import androidx.compose.material.icons.filled.Share
import androidx.compose.material3.Button
import androidx.compose.material3.ButtonDefaults
import androidx.compose.material3.Checkbox
import androidx.compose.material3.CheckboxDefaults
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.FilterChip
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.TextField
import androidx.compose.material3.TextFieldDefaults
import androidx.compose.runtime.Composable
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.draw.shadow
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.ImeAction
import androidx.compose.ui.text.input.KeyboardCapitalization
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp

private val TASK_LIST_ICONS = listOf("📋", "💼", "🏡", "🎯", "🧳", "🗓️")

/** The Tasks home creates lists; its composer never adds a task outside a list. */
@OptIn(ExperimentalFoundationApi::class)
@Composable
fun TaskListsScreen(onOpenList: (Long) -> Unit, onOpenGear: () -> Unit) {
    val pal = palFor(Tab.TASKS)
    val settings by SettingsStore.s.collectAsState()
    val allItems by ItemStore.items.collectAsState()
    val shareReceived by ShareStore.received.collectAsState()
    val shareBlocked by ShareStore.blocked.collectAsState()
    val sharePayload by ShareInbox.pending.collectAsState()
    val pendingTask = sharePayload?.takeIf { it.tab == Tab.TASKS }
    val shareOk = shareEnabledFor(Tab.TASKS, settings)
    val sharePendingCount = if (shareOk) pendingReceived(shareReceived, shareBlocked).count { it.tab == "TASKS" } else 0
    var showSharedHub by remember { mutableStateOf(false) }
    val lists = settings.taskLists
    val live = liveTaskLists(lists)
    val taskItems = remember(allItems) { allItems.filter { it.tab == Tab.TASKS && it.deletedAt == null } }
    val stats = remember(taskItems, lists) {
        liveTaskLists(lists).associate { list -> list.id to taskListStats(tasksInList(taskItems, list.id, lists), list.updatedAt) }
    }
    val ordered = sortedTaskLists(lists, settings.taskListSort) { stats[it.id]?.lastActivity ?: 0L }
    val unsorted = remember(taskItems, lists) { tasksInList(taskItems, UNSORTED_LIST_ID, lists) }
    var newName by remember { mutableStateOf("") }
    var editorOpen by remember { mutableStateOf(false) }
    var editorPrefill by remember { mutableStateOf("") }
    var menuFor by remember { mutableStateOf<TaskList?>(null) }
    var searchOpen by remember { mutableStateOf(false) }
    var query by remember { mutableStateOf("") }
    var sortOpen by remember { mutableStateOf(false) }
    val visible = ordered.filter { it.name.contains(query.trim(), ignoreCase = true) }
    val showUnsorted = unsorted.isNotEmpty() && "Unsorted".contains(query.trim(), ignoreCase = true)

    fun openEditor() {
        editorPrefill = newName.trim()
        editorOpen = true
    }

    Box(Modifier.fillMaxSize()) {
        Column(Modifier.fillMaxSize()) {
            GradientHeader(
                title = "Tasks",
                subtitle = "${live.size} list${if (live.size == 1) "" else "s"} · ${taskItems.count { !it.done }} active",
                pal = pal,
                leading = { ModeDrawerButton() },
                trailing = {
                    if (shareOk) SharedHubButton(sharePendingCount, tint = Color.White) { showSharedHub = true }
                    IconButton(onClick = { searchOpen = !searchOpen; query = "" }) {
                        Icon(if (searchOpen) Icons.Filled.Close else Icons.Filled.Search,
                            if (searchOpen) "Close search" else "Search lists", tint = Color.White)
                    }
                    IconButton(onClick = onOpenGear) { Icon(Icons.Filled.Settings, "Task settings", tint = Color.White) }
                }
            )
            DegradeBanner(Tab.TASKS, onOpenGear)
            pendingTask?.let { pending ->
                Row(
                    Modifier.fillMaxWidth().padding(horizontal = 12.dp, vertical = 8.dp)
                        .clip(RoundedCornerShape(12.dp)).background(pal.chipBg).padding(start = 12.dp, end = 4.dp, top = 6.dp, bottom = 6.dp),
                    verticalAlignment = Alignment.CenterVertically
                ) {
                    Column(Modifier.weight(1f)) {
                        Text("Choose a list for the shared task", color = pal.onChip,
                            style = MaterialTheme.typography.labelLarge, fontWeight = FontWeight.Bold)
                        Text(pending.title, color = InkSubtle, style = MaterialTheme.typography.bodySmall,
                            maxLines = 1, overflow = TextOverflow.Ellipsis)
                    }
                    TextButton(onClick = {
                        if (ShareInbox.pending.value == pending) ShareInbox.pending.value = null
                    }) { Text("Dismiss", color = pal.onChip) }
                }
            }
            if (searchOpen) OutlinedTextField(
                value = query, onValueChange = { query = it }, singleLine = true,
                placeholder = { Text("Search lists…") },
                leadingIcon = { Icon(Icons.Filled.Search, null, tint = pal.accent) },
                modifier = Modifier.fillMaxWidth().padding(start = 12.dp, end = 12.dp, top = 10.dp)
            )
            Row(
                Modifier.fillMaxWidth().padding(start = 14.dp, end = 12.dp, top = 8.dp),
                verticalAlignment = Alignment.CenterVertically
            ) {
                Text("Your lists", color = InkSubtle, style = MaterialTheme.typography.labelLarge, modifier = Modifier.weight(1f))
                Box {
                    TextButton(onClick = { sortOpen = true }) {
                        Text(if (settings.taskListSort == "AZ") "A–Z" else "Recent", color = pal.accent)
                        Icon(Icons.Filled.ArrowDropDown, "Sort lists", tint = pal.accent)
                    }
                    DropdownMenu(expanded = sortOpen, onDismissRequest = { sortOpen = false }) {
                        listOf("RECENT" to "Recent", "AZ" to "A–Z").forEach { (value, label) ->
                            DropdownMenuItem(text = { Text(label) }, onClick = {
                                SettingsStore.update { it.copy(taskListSort = value) }
                                sortOpen = false
                            })
                        }
                    }
                }
            }
            LazyColumn(
                modifier = Modifier.weight(1f),
                contentPadding = PaddingValues(start = 12.dp, end = 12.dp, top = 4.dp, bottom = 96.dp),
                verticalArrangement = Arrangement.spacedBy(10.dp)
            ) {
                if (live.isEmpty() && unsorted.isEmpty() && query.isBlank()) item(key = "first-list") {
                    TaskListsEmpty(pal, ::openEditor)
                }
                items(visible, key = { it.id }) { list ->
                    val st = stats[list.id] ?: taskListStats(emptyList(), list.updatedAt)
                    TaskListCard(list, st, pal, onOpen = { onOpenList(list.id) }, onMenu = { menuFor = list },
                        modifier = Modifier.animateItemPlacement())
                }
                if (showUnsorted) item(key = "unsorted") {
                    TaskUnsortedCard(unsorted.count { !it.done }, unsorted.size, pal) { onOpenList(UNSORTED_LIST_ID) }
                }
                if (query.isNotBlank() && visible.isEmpty() && !showUnsorted) item(key = "no-matches") {
                    Text("No matching lists", color = InkSubtle, style = MaterialTheme.typography.bodyMedium,
                        modifier = Modifier.fillMaxWidth().padding(vertical = 24.dp), textAlign = TextAlign.Center)
                }
            }
        }
        Row(
            Modifier.align(Alignment.BottomCenter).fillMaxWidth().padding(horizontal = 12.dp, vertical = 8.dp),
            verticalAlignment = Alignment.CenterVertically
        ) {
            Surface(Modifier.weight(1f), shape = RoundedCornerShape(50), color = SurfaceCard, shadowElevation = 10.dp) {
                TextField(
                    value = newName, onValueChange = { newName = it.take(60) },
                    placeholder = { Text("New list…", color = GreyIcon) }, singleLine = true,
                    colors = TextFieldDefaults.colors(
                        focusedContainerColor = Color.Transparent, unfocusedContainerColor = Color.Transparent,
                        focusedIndicatorColor = Color.Transparent, unfocusedIndicatorColor = Color.Transparent,
                        focusedTextColor = InkPrimary, unfocusedTextColor = InkPrimary, cursorColor = pal.accent
                    ),
                    keyboardOptions = KeyboardOptions(imeAction = ImeAction.Done, capitalization = KeyboardCapitalization.Words),
                    keyboardActions = KeyboardActions(onDone = { openEditor() }),
                    modifier = Modifier.fillMaxWidth().padding(start = 6.dp)
                )
            }
            Spacer(Modifier.width(8.dp))
            Box(
                Modifier.size(56.dp).shadow(8.dp, CircleShape).clip(CircleShape).background(pal.accent)
                    .clickable { openEditor() }, contentAlignment = Alignment.Center
            ) { Icon(Icons.Filled.Add, "Create list", tint = Color.White) }
        }
    }
    if (editorOpen) TaskListEditorSheet(null, editorPrefill, pal, onDismiss = { editorOpen = false }) { saved ->
        if (saved != null) {
            editorOpen = false
            newName = ""
            onOpenList(saved.id)
        }
    }
    menuFor?.let { list -> TaskListOptionsSheet(list, onDismiss = { menuFor = null }, onOpenList = onOpenList) }
    if (showSharedHub && shareOk) SharedHubSheet(Tab.TASKS, pal) { showSharedHub = false }
}

@OptIn(ExperimentalFoundationApi::class)
@Composable
private fun TaskListCard(
    list: TaskList, stats: TaskListStats, pal: TabPalette, onOpen: () -> Unit, onMenu: () -> Unit,
    modifier: Modifier = Modifier
) {
    val total = stats.active + stats.done
    Row(
        modifier.fillMaxWidth().clip(RoundedCornerShape(14.dp)).background(SurfaceCard)
            .combinedClickable(onClick = onOpen, onLongClick = onMenu).padding(12.dp),
        verticalAlignment = Alignment.Top
    ) {
        Box(Modifier.size(42.dp).clip(RoundedCornerShape(12.dp)).background(pal.chipBg), contentAlignment = Alignment.Center) {
            Text(list.icon ?: "📋", style = MaterialTheme.typography.titleLarge)
        }
        Spacer(Modifier.width(10.dp))
        Column(Modifier.weight(1f)) {
            Row(verticalAlignment = Alignment.CenterVertically) {
                Text(list.name, style = MaterialTheme.typography.titleMedium, fontWeight = FontWeight.Bold,
                    color = InkPrimary, maxLines = 1, overflow = TextOverflow.Ellipsis, modifier = Modifier.weight(1f, fill = false))
                if (list.pinned) {
                    Spacer(Modifier.width(4.dp))
                    Icon(Icons.Filled.PushPin, "Pinned", tint = pal.accent, modifier = Modifier.size(14.dp))
                }
            }
            Text("${stats.active} active · ${stats.done} done", style = MaterialTheme.typography.bodySmall, color = InkSubtle)
            if (total > 0) {
                Spacer(Modifier.height(8.dp))
                LinearProgressIndicator(
                    progress = { stats.done.toFloat() / total }, color = pal.accent, trackColor = pal.chipBg,
                    modifier = Modifier.fillMaxWidth().height(5.dp).clip(RoundedCornerShape(3.dp))
                )
            }
        }
        Spacer(Modifier.width(6.dp))
        Column(horizontalAlignment = Alignment.CenterHorizontally) {
            Box(
                Modifier.clip(RoundedCornerShape(50)).background(if (stats.active > 0) pal.accent else pal.chipBg)
                    .padding(horizontal = 9.dp, vertical = 3.dp)
            ) {
                Text("${stats.active}", color = if (stats.active > 0) Color.White else pal.onChip,
                    style = MaterialTheme.typography.labelMedium, fontWeight = FontWeight.Bold)
            }
            IconButton(onClick = onMenu, modifier = Modifier.size(40.dp)) {
                Icon(Icons.Filled.MoreVert, "Options for ${list.name}", tint = GreyIcon)
            }
        }
    }
}

@Composable
private fun TaskUnsortedCard(active: Int, total: Int, pal: TabPalette, onOpen: () -> Unit) {
    Row(
        Modifier.fillMaxWidth().clip(RoundedCornerShape(14.dp)).border(1.dp, InkFaint, RoundedCornerShape(14.dp))
            .clickable(onClick = onOpen).padding(12.dp), verticalAlignment = Alignment.CenterVertically
    ) {
        Box(Modifier.size(42.dp).clip(RoundedCornerShape(12.dp)).background(SurfaceCard), contentAlignment = Alignment.Center) {
            Text("📥", style = MaterialTheme.typography.titleLarge)
        }
        Spacer(Modifier.width(10.dp))
        Column(Modifier.weight(1f)) {
            Text("Unsorted", style = MaterialTheme.typography.titleMedium, fontWeight = FontWeight.Bold, color = InkPrimary)
            Text("$total task${if (total == 1) "" else "s"} without a list · tap to sort",
                style = MaterialTheme.typography.bodySmall, color = InkSubtle)
        }
        Text("$active", color = pal.onChip, fontWeight = FontWeight.Bold, style = MaterialTheme.typography.labelMedium,
            modifier = Modifier.clip(RoundedCornerShape(50)).background(pal.chipBg).padding(horizontal = 9.dp, vertical = 3.dp))
    }
}

@Composable
private fun TaskListsEmpty(pal: TabPalette, onCreate: () -> Unit) {
    Column(
        Modifier.fillMaxWidth().padding(horizontal = 24.dp, vertical = 42.dp),
        horizontalAlignment = Alignment.CenterHorizontally, verticalArrangement = Arrangement.spacedBy(12.dp)
    ) {
        Box(Modifier.size(76.dp).clip(RoundedCornerShape(22.dp)).background(pal.chipBg), contentAlignment = Alignment.Center) {
            Icon(Icons.Filled.CreateNewFolder, null, tint = pal.accent, modifier = Modifier.size(36.dp))
        }
        Text("Create your first list", style = MaterialTheme.typography.titleLarge, color = InkPrimary,
            fontWeight = FontWeight.Bold, textAlign = TextAlign.Center)
        Text("Give your tasks a home, then add tasks inside it.", style = MaterialTheme.typography.bodyMedium,
            color = InkSubtle, textAlign = TextAlign.Center)
        Button(onClick = onCreate, colors = ButtonDefaults.buttonColors(containerColor = pal.accent)) {
            Text("Create first list", color = Color.White, fontWeight = FontWeight.Bold)
        }
    }
}

/** Shared with the list detail page's rename action. */
@OptIn(ExperimentalMaterial3Api::class, ExperimentalLayoutApi::class)
@Composable
fun TaskListEditorSheet(
    list: TaskList?, prefillName: String, pal: TabPalette,
    onDismiss: () -> Unit, onSaved: (TaskList?) -> Unit
) {
    val context = LocalContext.current
    val settings by SettingsStore.s.collectAsState()
    var name by remember(list?.id) { mutableStateOf(list?.name ?: prefillName) }
    var icon by remember(list?.id) { mutableStateOf(list?.icon ?: "📋") }
    var pinned by remember(list?.id) { mutableStateOf(list?.pinned ?: false) }
    var saveError by remember { mutableStateOf<String?>(null) }
    val trimmed = name.trim()
    val duplicate = taskListNamed(settings.taskLists, trimmed)?.takeIf { it.id != list?.id }
    val error = when {
        trimmed.isEmpty() -> "Give your list a name."
        trimmed.equals("Unsorted", ignoreCase = true) -> "Choose another name. Unsorted holds tasks without a list."
        duplicate != null -> "A list with this name already exists."
        else -> saveError
    }
    fun save() {
        if (error != null) return
        val saved = if (list == null) TaskListStore.create(context, trimmed, icon, pinned)
        else if (TaskListStore.edit(context, list, trimmed, icon, pinned)) TaskListStore.get(list.id) else null
        if (saved == null) saveError = "Couldn't save this list. Check its name and try again."
        else onSaved(saved)
    }

    EditorSheet(
        title = if (list == null) "New list" else "Edit list", accent = pal.accent, onDismiss = onDismiss,
        actions = {
            // The longer create label has extra room, including on smaller phones.
            Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                OutlinedButton(onClick = onDismiss, modifier = Modifier.weight(0.7f).height(50.dp),
                    contentPadding = PaddingValues(horizontal = 6.dp)) { Text("Cancel", color = InkSubtle, fontWeight = FontWeight.Bold) }
                Button(onClick = { save() }, enabled = error == null,
                    colors = ButtonDefaults.buttonColors(containerColor = pal.accent),
                    contentPadding = PaddingValues(horizontal = 8.dp), modifier = Modifier.weight(1.3f).height(50.dp)) {
                    Text(if (list == null) "Create & add tasks" else "Save list", color = Color.White,
                        fontWeight = FontWeight.Bold, maxLines = 1)
                }
            }
        }
    ) {
        OutlinedTextField(
            value = name, onValueChange = { name = it.take(60); saveError = null }, label = { Text("List name") },
            placeholder = { Text("e.g. Weekend plans") }, singleLine = true,
            isError = error != null && name.isNotBlank(),
            supportingText = { if (error != null && name.isNotBlank()) Text(error) },
            keyboardOptions = KeyboardOptions(capitalization = KeyboardCapitalization.Words, imeAction = ImeAction.Done),
            keyboardActions = KeyboardActions(onDone = { save() }), modifier = Modifier.fillMaxWidth()
        )
        Spacer(Modifier.height(10.dp))
        Text("List icon", style = MaterialTheme.typography.labelLarge, fontWeight = FontWeight.SemiBold, color = InkSubtle)
        FlowRow(horizontalArrangement = Arrangement.spacedBy(8.dp), verticalArrangement = Arrangement.spacedBy(4.dp)) {
            TASK_LIST_ICONS.forEach { choice ->
                FilterChip(selected = icon == choice, onClick = { icon = choice },
                    label = { Text(choice, style = MaterialTheme.typography.titleLarge) })
            }
        }
        Row(Modifier.fillMaxWidth().clickable { pinned = !pinned }, verticalAlignment = Alignment.CenterVertically) {
            Checkbox(checked = pinned, onCheckedChange = { pinned = it }, colors = CheckboxDefaults.colors(checkedColor = pal.accent))
            Text("Pin list to top", style = MaterialTheme.typography.bodyMedium, color = InkPrimary)
        }
    }
}

/** Reusable options for home cards and the list detail header. */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun TaskListOptionsSheet(
    list: TaskList, onDismiss: () -> Unit, onOpenList: ((Long) -> Unit)? = null, onDeleted: () -> Unit = {},
    onShare: (() -> Unit)? = null, onCompleteAll: (() -> Unit)? = null
) {
    val context = LocalContext.current
    val pal = palFor(Tab.TASKS)
    val allItems by ItemStore.items.collectAsState()
    var editOpen by remember(list.id) { mutableStateOf(false) }
    var deleteOpen by remember(list.id) { mutableStateOf(false) }
    val items = remember(allItems, list.id) { TaskListStore.itemsOf(list.id) }
    when {
        editOpen -> TaskListEditorSheet(list, "", pal, onDismiss = onDismiss) { onDismiss() }
        deleteOpen -> TaskDeleteListSheet(list, items.size, onDismiss = onDismiss) {
            onDismiss()
            onDeleted()
        }
        else -> EditorSheet(
            title = "${list.icon ?: "📋"} ${list.name}", accent = pal.accent, onDismiss = onDismiss,
            actions = { TextButton(onClick = onDismiss, modifier = Modifier.fillMaxWidth()) { Text("Close", color = pal.accent) } }
        ) {
            Text("${items.count { !it.done }} active · ${items.count { it.done }} done",
                style = MaterialTheme.typography.bodySmall, color = InkSubtle)
            TaskListMenuAction(Icons.Filled.PushPin, if (list.pinned) "Unpin list" else "Pin to top", pal.accent) {
                TaskListStore.togglePin(context, list)
                onDismiss()
            }
            TaskListMenuAction(Icons.Filled.Edit, "Rename & icon", pal.accent) { editOpen = true }
            if (onOpenList != null) TaskListMenuAction(Icons.Filled.FolderOpen, "Open list", pal.accent) { onDismiss(); onOpenList(list.id) }
            if (onShare != null) TaskListMenuAction(Icons.Filled.Share, "Share list", pal.accent) { onDismiss(); onShare() }
            if (onCompleteAll != null) TaskListMenuAction(Icons.Filled.CheckCircle, "Complete all", pal.accent) { onDismiss(); onCompleteAll() }
            TaskListMenuAction(Icons.Filled.Delete, "Delete list…", OverdueRed) { deleteOpen = true }
        }
    }
}

@Composable
private fun TaskListMenuAction(icon: androidx.compose.ui.graphics.vector.ImageVector, label: String, tint: Color, onClick: () -> Unit) {
    TextButton(onClick = onClick, modifier = Modifier.fillMaxWidth(), contentPadding = PaddingValues(vertical = 12.dp)) {
        Icon(icon, null, tint = tint, modifier = Modifier.size(20.dp))
        Spacer(Modifier.width(12.dp))
        Text(label, color = tint, modifier = Modifier.weight(1f), textAlign = TextAlign.Start)
    }
}

@OptIn(ExperimentalMaterial3Api::class)
@Composable
private fun TaskDeleteListSheet(list: TaskList, taskCount: Int, onDismiss: () -> Unit, onDeleted: () -> Unit) {
    val context = LocalContext.current
    EditorSheet(
        title = "Delete list?", accent = OverdueRed, onDismiss = onDismiss,
        actions = {
            EditorActionRow(accent = OverdueRed, onCancel = onDismiss, saveLabel = "Delete list", onSave = {
                if (TaskListStore.delete(context, list)) {
                    Feedback.toast(context, "List deleted · Tasks kept in Unsorted")
                    onDeleted()
                }
            })
        }
    ) {
        Text("Delete “${list.name}”? Its $taskCount task${if (taskCount == 1) "" else "s"} will stay in Unsorted, with their reminders and completion status.",
            style = MaterialTheme.typography.bodyMedium, color = InkSubtle)
    }
}
