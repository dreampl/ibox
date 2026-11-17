import SwiftUI

@main
struct IBOXApp: App {
    var body: some Scene {
        WindowGroup { ContentView() }
    }
}

struct ContentView: View {
    @State private var connected = false
    var body: some View {
        VStack(spacing: 24) {
            Text("IBOX").font(.largeTitle.bold())
            Button(connected ? "Disconnect / قطع اتصال" : "Connect / اتصال") {
                connected.toggle() // TODO: NETunnelProviderManager + libbox packet tunnel
            }
            .buttonStyle(.borderedProminent)
        }
        .padding()
    }
}
