import SwiftUI

struct ContentView: View {
    @ViewBuilder
    var body: some View {
        if let workingDirectory = TerminalWorkspaceLaunch.workingDirectory() {
            TerminalWorkspaceView(workingDirectory: workingDirectory)
        } else {
            TerminalDirectoryRequiredView()
        }
    }
}

private struct TerminalDirectoryRequiredView: View {
    var body: some View {
        ContentUnavailableView {
            Label("Terminal directory required", systemImage: "folder.badge.questionmark")
        } description: {
            Text("Launch gabCode with --terminal-directory followed by an accessible absolute directory path. No shell was started.")
        }
        .frame(minWidth: 760, minHeight: 640)
        .accessibilityElement(children: .combine)
        .accessibilityIdentifier("terminal-directory-required")
    }
}

#Preview {
    ContentView()
}
