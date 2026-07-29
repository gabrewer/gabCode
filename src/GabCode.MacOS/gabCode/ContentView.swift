//
//  ContentView.swift
//  gabCode
//
//  Created by Gregory Brewer on 7/28/26.
//

import SwiftUI

struct ContentView: View {
    var body: some View {
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
