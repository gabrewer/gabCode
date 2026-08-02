import SwiftUI

struct TerminalSettingsView: View {
    @EnvironmentObject private var preferences: TerminalFontPreferenceStore
    @State private var pointSizeText = ""

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
                    .onSubmit { savePointSize() }
                    .accessibilityValue(pointSizeText)
                    .accessibilityIdentifier("terminal-font-size")

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

    private func savePointSize() {
        guard let value = Double(pointSizeText) else {
            pointSizeText = formatted(preferences.effectiveSelection.pointSize)
            return
        }
        let validSelection: TerminalFontSelection?
        switch preferences.effectiveSelection.face {
        case .systemDefault:
            validSelection = TerminalFontSelection.systemDefault(pointSize: CGFloat(value))
        case let .named(name):
            validSelection = TerminalFontSelection.named(postScriptName: name, pointSize: CGFloat(value))
        }
        guard let validSelection else {
            pointSizeText = formatted(preferences.effectiveSelection.pointSize)
            return
        }
        preferences.save(validSelection)
    }

    private func formatted(_ value: CGFloat) -> String {
        value == value.rounded() ? String(Int(value)) : String(format: "%.1f", value)
    }
}
