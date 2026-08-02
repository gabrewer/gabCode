//
//  gabCodeApp.swift
//  gabCode
//
//  Created by Gregory Brewer on 7/28/26.
//

import SwiftUI

@main
struct gabCodeApp: App {
    @NSApplicationDelegateAdaptor(GabCodeAppDelegate.self) private var appDelegate
    @StateObject private var fontPreference: TerminalFontPreferenceStore

    init() {
        _fontPreference = StateObject(
            wrappedValue: TerminalFontPreferenceStore(defaults: Self.fontPreferenceDefaults())
        )
    }

    var body: some Scene {
        WindowGroup("gabCode", id: "main") {
            ContentView()
                .environmentObject(fontPreference)
        }
        .defaultSize(width: 760, height: 640)
        .commands {
            TerminalWorkspaceCommands()
        }

        Settings {
            TerminalSettingsView()
                .environmentObject(fontPreference)
        }
    }

    private static func fontPreferenceDefaults(
        environment: [String: String] = ProcessInfo.processInfo.environment
    ) -> UserDefaults {
        #if DEBUG
        if
            let suiteName = environment["GABCODE_UI_TEST_PREFERENCE_SUITE"],
            !suiteName.isEmpty,
            let isolatedDefaults = UserDefaults(suiteName: suiteName)
        {
            return isolatedDefaults
        }
        #endif
        return .standard
    }
}
