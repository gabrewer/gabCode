import SwiftUI

@MainActor
struct ContentView: View {
    @EnvironmentObject private var fontPreference: TerminalFontPreferenceStore
    @StateObject private var projectController: WorkspaceProjectController

    init() {
        _projectController = StateObject(
            wrappedValue: WorkspaceProjectController(defaults: Self.workspaceDefaults())
        )
    }

    var body: some View {
        WorkspaceProjectView(controller: projectController)
            .environmentObject(fontPreference)
            .accessibilityIdentifier("workspace-project-surface")
    }

    private static func workspaceDefaults() -> UserDefaults {
        #if DEBUG
        if
            let suiteName = ProcessInfo.processInfo.environment["GABCODE_UI_TEST_PREFERENCE_SUITE"],
            !suiteName.isEmpty,
            let isolatedDefaults = UserDefaults(suiteName: suiteName)
        {
            return isolatedDefaults
        }
        #endif
        return .standard
    }
}

#Preview {
    ContentView()
        .environmentObject(TerminalFontPreferenceStore())
}
