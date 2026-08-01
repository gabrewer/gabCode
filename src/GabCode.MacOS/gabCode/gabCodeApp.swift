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

    var body: some Scene {
        Window("gabCode", id: "main") {
            ContentView()
        }
        .defaultSize(width: 760, height: 640)
        .commands {
            TerminalWorkspaceCommands()
        }
    }
}
