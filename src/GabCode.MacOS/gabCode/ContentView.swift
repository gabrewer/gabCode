import SwiftUI

struct ContentView: View {
    @ViewBuilder
    var body: some View {
        if let workingDirectory = TerminalWorkspaceLaunch.workingDirectory() {
            TerminalWorkspaceView(workingDirectory: workingDirectory)
        } else {
            TerminalDirectoryUnavailableView()
        }
    }
}

private struct TerminalDirectoryUnavailableView: View {
    var body: some View {
        ContentUnavailableView {
            Label("Terminal directory unavailable", systemImage: "folder.badge.questionmark")
        } description: {
            Text("gabCode could not access the requested terminal directory or your home directory. No shell was started.")
        }
        .frame(minWidth: 760, minHeight: 640)
        .accessibilityElement(children: .combine)
        .accessibilityIdentifier("terminal-directory-unavailable")
    }
}

#Preview {
    ContentView()
}
