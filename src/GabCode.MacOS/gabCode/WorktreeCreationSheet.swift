import AppKit
import SwiftUI

@MainActor
struct WorktreeCreationSheet: View {
    let controller: WorkspaceProjectController
    let base: WorktreeCreationBase
    let selectedWorktreeBranch: String?
    @Binding var isPresented: Bool
    @State private var form: WorktreeCreationFormState
    @State private var choices: [WorktreeBranchChoice] = []
    @State private var selectedExistingBranch = ""
    @State private var isWorking = false
    @State private var localError: String?
    @State private var isShowingBranchPicker = false
    @State private var isLoadingBranches = false

    init(
        controller: WorkspaceProjectController,
        base: WorktreeCreationBase,
        selectedWorktreeBranch: String?,
        isPresented: Binding<Bool>
    ) {
        self.controller = controller
        self.base = base
        self.selectedWorktreeBranch = selectedWorktreeBranch
        _isPresented = isPresented
        let root = controller.projectRoot?.appendingPathComponent("wt", isDirectory: true)
            ?? URL(fileURLWithPath: NSTemporaryDirectory(), isDirectory: true)
        _form = State(initialValue: WorktreeCreationFormState(name: "", under: root, base: base))
    }

    private var isExistingMode: Bool {
        if case .existingLocalBranch = base { return true }
        if case .existingRemoteBranch = base { return true }
        return false
    }

    private var effectiveBase: WorktreeCreationBase {
        guard isExistingMode, let choice = choices.first(where: { choice in
            let key = choice.isRemote ? "remote:\(choice.remote ?? "")/\(choice.name)" : "local:\(choice.name)"
            return key == selectedExistingBranch && choice.name == form.branch
        }) else {
            return isExistingMode && !form.branch.isEmpty ? .existingLocalBranch(form.branch) : base
        }
        if choice.isRemote, let remote = choice.remote {
            return .existingRemoteBranch(remote: remote, branch: choice.name)
        }
        return .existingLocalBranch(choice.name)
    }

    private var canCreate: Bool {
        WorktreeActionPreview.isValidBranchName(form.branch) &&
            !form.location.path.isEmpty &&
            FileManager.default.fileExists(atPath: form.location.deletingLastPathComponent().path) &&
            !FileManager.default.fileExists(atPath: form.location.path) &&
            (!isExistingMode || !form.branch.isEmpty)
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 16) {
            Text("Create Worktree").font(.title2).accessibilityAddTraits(.isHeader)
            Form {
                TextField("Name", text: $form.name)
                    .accessibilityLabel("Worktree name")
                    .onChange(of: form.name) { _, _ in
                        if !isExistingMode { form.refreshDefaults() }
                    }
                if isExistingMode {
                    Section("Existing branch") {
                        Button(isLoadingBranches ? "Loading branches…" : "Choose from local or remote branches…") {
                            Task {
                                await loadChoices()
                                isShowingBranchPicker = !choices.isEmpty
                            }
                        }
                        .accessibilityIdentifier("choose-existing-branch")
                        .buttonStyle(.borderedProminent)
                        .disabled(isLoadingBranches)
                        .popover(isPresented: $isShowingBranchPicker, arrowEdge: .bottom) {
                            branchPicker
                        }
                    }
                }
                TextField("Branch", text: $form.branch)
                    .accessibilityLabel("Branch name")
                HStack {
                    TextField("Location", text: Binding(
                        get: { form.location.path },
                        set: { form.location = URL(fileURLWithPath: $0, isDirectory: true) }
                    ))
                    Button("Browse…") { browseForLocation() }
                }
                if case .workspaceSelectedBranch = base {
                    Toggle("Use the latest remote version of the workspace branch", isOn: $form.useLatestRemote)
                        .accessibilityHint("Fetches the configured upstream without changing the existing workspace.")
                }
                Toggle("Create a VS Code workspace file", isOn: $form.createVSCodeWorkspace)
                Toggle("Open in VS Code after creation", isOn: $form.openVSCodeAfterCreation)
            }
            Text("Branch and location are editable previews. gabCode will not overwrite an existing folder.")
                .font(.caption)
                .foregroundStyle(.secondary)
            if let localError { Text(localError).foregroundStyle(.red).accessibilityLabel("Creation error: \(localError)") }
            if let error = controller.worktreeActionError { Text(String(describing: error)).foregroundStyle(.red).accessibilityLabel("Creation error: \(String(describing: error))") }
            HStack {
                Spacer()
                Button("Cancel") { isPresented = false }
                    .keyboardShortcut(.cancelAction)
                Button("Create") { create() }
                    .keyboardShortcut(.defaultAction)
                    .disabled(!canCreate || isWorking)
            }
        }
        .padding(24)
        .frame(minWidth: 540, minHeight: 460)
        .onAppear { if isExistingMode { Task { await loadChoices() } } }
    }

    @ViewBuilder
    private var branchPicker: some View {
        VStack(alignment: .leading, spacing: 8) {
            Text("Existing branches")
                .font(.headline)
            if choices.isEmpty {
                Text("No local or remote branches were found.")
                    .foregroundStyle(.secondary)
            } else {
                ScrollView {
                    VStack(alignment: .leading, spacing: 4) {
                        ForEach(choices, id: \.self) { choice in
                            Button {
                                selectedExistingBranch = choiceID(choice)
                                form.branch = choice.name
                                isShowingBranchPicker = false
                            } label: {
                                HStack {
                                    Text(choiceTitle(choice))
                                    Spacer()
                                    if selectedExistingBranch == choiceID(choice) {
                                        Image(systemName: "checkmark")
                                    }
                                }
                            }
                            .buttonStyle(.plain)
                            .disabled(choice.attachedPath != nil)
                            .opacity(choice.attachedPath == nil ? 1 : 0.5)
                        }
                    }
                }
                .frame(width: 280, height: 180)
            }
            Button("Refresh Branches") { Task { await loadChoices() } }
        }
        .padding()
    }

    private func choiceID(_ choice: WorktreeBranchChoice) -> String {
        choice.isRemote ? "remote:\(choice.remote ?? "")/\(choice.name)" : "local:\(choice.name)"
    }

    private func choiceTitle(_ choice: WorktreeBranchChoice) -> String {
        choice.isRemote ? "\(choice.name) (\(choice.remote ?? "remote"))" : choice.name
    }

    private func loadChoices() async {
        isLoadingBranches = true
        choices = await controller.branchChoices()
        isLoadingBranches = false
    }

    private func browseForLocation() {
        let panel = NSOpenPanel()
        panel.canChooseFiles = false
        panel.canChooseDirectories = true
        panel.allowsMultipleSelection = false
        panel.prompt = "Choose Location"
        panel.begin { response in
            if response == .OK, let url = panel.url { form.location = url }
        }
    }

    private func create() {
        guard let root = controller.projectRoot else { return }
        isWorking = true
        localError = nil
        let request = WorktreeCreationRequest(
            name: form.name,
            branch: form.branch,
            location: form.location,
            base: effectiveBase,
            useLatestRemote: form.useLatestRemote
        )
        Task {
            let succeeded = await controller.createWorktree(request: request, selectedWorktreeBranch: selectedWorktreeBranch)
            guard succeeded else { isWorking = false; return }
            if form.createVSCodeWorkspace {
                do { try createVSCodeWorkspace(at: form.location) }
                catch { localError = "Worktree created, but the VS Code workspace file could not be created: \(error.localizedDescription)" }
            }
            if form.openVSCodeAfterCreation { openInVSCode(form.location) }
            if controller.worktrees.contains(where: { $0.path.standardizedFileURL == form.location.standardizedFileURL }) {
                isPresented = false
            }
            _ = root
            isWorking = false
        }
    }

    private func createVSCodeWorkspace(at location: URL) throws {
        let file = location.appendingPathComponent("\(location.lastPathComponent).code-workspace")
        let data = try JSONSerialization.data(withJSONObject: ["folders": [["path": "."]]], options: [.prettyPrinted, .sortedKeys])
        try data.write(to: file, options: .atomic)
    }

    private func openInVSCode(_ path: URL) {
        guard let application = NSWorkspace.shared.urlForApplication(withBundleIdentifier: "com.microsoft.VSCode") else {
            localError = "Visual Studio Code could not be found."
            return
        }
        let workspace = path.appendingPathComponent("\(path.lastPathComponent).code-workspace")
        let target = form.createVSCodeWorkspace && FileManager.default.fileExists(atPath: workspace.path) ? workspace : path
        NSWorkspace.shared.open([target], withApplicationAt: application, configuration: NSWorkspace.OpenConfiguration())
    }
}
