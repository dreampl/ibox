#if os(iOS)
import SwiftUI
import IBOXCore

@main
struct IBOXApp: App {
    @StateObject private var model = AppModel()

    var body: some Scene {
        WindowGroup {
            ContentView()
                .environmentObject(model)
                .environment(\.layoutDirection, L10n.isPersian ? .rightToLeft : .leftToRight)
        }
    }
}
#endif
