import SwiftUI

struct TerminalSettingsView: View {
    @EnvironmentObject private var preferences: TerminalFontPreferenceStore
    @State private var pointSizeText = ""
    @FocusState private var isPointSizeFocused: Bool

    var body: some View {
        Form {
            Section("Terminal font") {
                Picker("Font face", selection: faceBinding) {
                    Text("System Monospaced").tag(nil as String?)
                    ForEach(preferences.catalog.selectableFaces) { face in
                        Text(face.displayName).tag(Optional(face.postScriptName))
                    }
                }
                .accessibilityIdentifier("terminal-font-face")

                TextField("Point size", text: $pointSizeText)
                    .textFieldStyle(.roundedBorder)
                    .focused($isPointSizeFocused)
                    .onChange(of: pointSizeText) { _, text in
                        savePointSizeIfValid(text)
                    }
                    .onSubmit { restoreEffectivePointSizeText() }
                    .accessibilityValue(pointSizeText)
                    .accessibilityIdentifier("terminal-font-size")

                Text("Enter a value from 8 to 72 points.")
                    .font(.caption)
                    .foregroundStyle(.secondary)

                HStack {
                    Button("Restore System Default") {
                        preferences.restoreSystemDefault()
                        pointSizeText = formatted(preferences.effectiveSelection.pointSize)
                    }
                    Spacer()
                    Text("Effective: \(effectiveFaceName), \(formatted(preferences.effectiveSelection.pointSize)) pt")
                        .foregroundStyle(.secondary)
                        .accessibilityIdentifier("terminal-font-effective-value")
                }
            }

            Section("Preview") {
                VStack(alignment: .leading, spacing: 8) {
                    Text("Aa Bb Cc 0123 你好 • Powerline:  ")
                        .font(Font(preferences.effectiveFont))
                    Text("Terminal font preview. Private-use glyphs are shown when supplied by the selected font.")
                        .font(.caption)
                        .foregroundStyle(.secondary)
                }
                .accessibilityElement(children: .combine)
                .accessibilityLabel("Terminal font preview")
                .accessibilityValue("Ordinary text, numbers, Unicode, and representative Powerline glyphs")
                .accessibilityIdentifier("terminal-font-preview")
            }
        }
        .formStyle(.grouped)
        .frame(width: 520)
        .onAppear { pointSizeText = formatted(preferences.effectiveSelection.pointSize) }
        .onChange(of: preferences.effectiveSelection.pointSize) { _, value in
            pointSizeText = formatted(value)
        }
        .onChange(of: isPointSizeFocused) { _, isFocused in
            if !isFocused {
                restoreEffectivePointSizeText()
            }
        }
    }

    private var faceBinding: Binding<String?> {
        Binding(
            get: {
                if case let .named(name) = preferences.effectiveSelection.face { return name }
                return nil
            },
            set: { value in
                let face: TerminalFontSelection.Face = value.map { .named(postScriptName: $0) } ?? .systemDefault
                preferences.save(TerminalFontSelection(face: face, pointSize: preferences.effectiveSelection.pointSize))
            }
        )
    }

    private var effectiveFaceName: String {
        switch preferences.effectiveSelection.face {
        case .systemDefault: return "System Monospaced"
        case let .named(name): return preferences.catalog.selectableFaces.first { $0.postScriptName == name }?.displayName ?? name
        }
    }

    private func savePointSizeIfValid(_ text: String) {
        guard let selection = TerminalPointSizeInput.selection(
            from: text,
            face: preferences.effectiveSelection.face
        ) else {
            return
        }
        preferences.save(selection)
    }

    private func restoreEffectivePointSizeText() {
        pointSizeText = formatted(preferences.effectiveSelection.pointSize)
    }

    private func formatted(_ value: CGFloat) -> String {
        value == value.rounded() ? String(Int(value)) : String(format: "%.1f", value)
    }
}

enum TerminalPointSizeInput {
    static func selection(
        from text: String,
        face: TerminalFontSelection.Face
    ) -> TerminalFontSelection? {
        guard let value = Double(text) else {
            return nil
        }

        switch face {
        case .systemDefault:
            return TerminalFontSelection.systemDefault(pointSize: CGFloat(value))
        case let .named(postScriptName):
            return TerminalFontSelection.named(
                postScriptName: postScriptName,
                pointSize: CGFloat(value)
            )
        }
    }
}
