//
//  ContentView.swift
//  gabCode
//
//  Created by Gregory Brewer on 7/28/26.
//

import SwiftUI

struct ContentView: View {
    @ViewBuilder
    var body: some View {
#if DEBUG
        if let workingDirectory = TerminalFeasibilityLaunch.workingDirectory() {
            TerminalFeasibilityView(workingDirectory: workingDirectory)
        } else {
            foundationContent
        }
#else
        foundationContent
#endif
    }

    private var foundationContent: some View {
        VStack(spacing: 12) {
            Text("gabCode")
                .font(.largeTitle)
                .fontWeight(.semibold)
            Text("macOS foundation ready.")
        }
        .padding()
    }
}

#Preview {
    ContentView()
}
