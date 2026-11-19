#if os(iOS)
import SwiftUI
import UIKit
import IBOXCore

struct ContentView: View {
    @EnvironmentObject private var model: AppModel
    @State private var showingAdd = false

    var body: some View {
        NavigationStack {
            List {
                Section { ConnectCard(tunnel: model.tunnel) }

                if model.state.subscriptions.isEmpty {
                    Section { Text(L10n.noServers).foregroundStyle(.secondary) }
                }

                ForEach(model.state.subscriptions) { sub in
                    Section {
                        ForEach(sub.profiles) { p in
                            ServerRow(profile: p, selected: p.id == model.selected?.id)
                                .contentShape(Rectangle())
                                .onTapGesture { model.select(p) }
                        }
                    } header: {
                        SubscriptionHeader(sub: sub)
                    }
                }
            }
            .navigationTitle("IBOX")
            .toolbar {
                ToolbarItem(placement: .primaryAction) {
                    Button { showingAdd = true } label: { Image(systemName: "plus") }
                        .accessibilityLabel(L10n.addTitle)
                }
            }
            .overlay { if model.busy { ProgressView() } }
            .sheet(isPresented: $showingAdd) { AddServersView() }
            .alert(model.message ?? "", isPresented: Binding(get: { model.message != nil }, set: { if !$0 { model.message = nil } })) {
                Button("OK", role: .cancel) {}
            }
        }
    }
}

private struct ConnectCard: View {
    @EnvironmentObject private var model: AppModel
    @ObservedObject var tunnel: TunnelController

    var body: some View {
        VStack(spacing: 12) {
            Text(tunnel.statusText).font(.headline)
                .foregroundStyle(tunnel.status == .connected ? .green : .secondary)
            if let p = model.selected {
                Text(p.displayName).font(.subheadline).lineLimit(1)
            }
            Button {
                model.toggleConnection()
            } label: {
                Text(tunnel.isActive ? L10n.disconnect : L10n.connect)
                    .frame(maxWidth: .infinity)
                    .padding(.vertical, 6)
            }
            .buttonStyle(.borderedProminent)
            .tint(tunnel.isActive ? .red : .accentColor)
            .disabled(model.selected == nil)
        }
        .frame(maxWidth: .infinity)
        .padding(.vertical, 8)
    }
}

private struct SubscriptionHeader: View {
    @EnvironmentObject private var model: AppModel
    let sub: Subscription

    var body: some View {
        HStack {
            Text(sub.id == ServerState.manualSubscriptionId ? L10n.manual : sub.name)
            Spacer()
            Menu {
                if sub.url != nil {
                    Button(L10n.update) { Task { await model.refresh(sub) } }
                }
                Button(L10n.delete, role: .destructive) { model.remove(sub) }
            } label: {
                Image(systemName: "ellipsis.circle")
            }
        }
    }
}

private struct ServerRow: View {
    let profile: Profile
    let selected: Bool

    var body: some View {
        HStack {
            VStack(alignment: .leading, spacing: 2) {
                Text(profile.displayName).lineLimit(1)
                HStack(spacing: 6) {
                    Text(profile.proto.rawValue.uppercased())
                    if profile.stream.type != .tcp { Text(profile.stream.type.rawValue) }
                    if profile.tls.isReality { Text("reality") } else if profile.tls.enabled { Text("tls") }
                }
                .font(.caption).foregroundStyle(.secondary)
                if !SingBoxConfig.isSupported(profile) {
                    Text(L10n.needsXray).font(.caption).foregroundStyle(.orange)
                }
            }
            Spacer()
            if selected { Image(systemName: "checkmark").foregroundStyle(.tint) }
        }
    }
}

private struct AddServersView: View {
    @EnvironmentObject private var model: AppModel
    @Environment(\.dismiss) private var dismiss
    @State private var text = ""

    var body: some View {
        NavigationStack {
            Form {
                Section(footer: Text(L10n.addHint)) {
                    TextEditor(text: $text)
                        .frame(minHeight: 140)
                        .font(.system(.footnote, design: .monospaced))
                        .textInputAutocapitalization(.never)
                        .autocorrectionDisabled()
                        .environment(\.layoutDirection, .leftToRight)
                    Button(L10n.pasteClipboard) { text = UIPasteboard.general.string ?? text }
                }
            }
            .navigationTitle(L10n.addTitle)
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .cancellationAction) { Button(L10n.cancel) { dismiss() } }
                ToolbarItem(placement: .confirmationAction) {
                    Button(L10n.add) {
                        let input = text
                        dismiss()
                        Task { await model.add(input) }
                    }
                    .disabled(text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty)
                }
            }
        }
    }
}
#endif
