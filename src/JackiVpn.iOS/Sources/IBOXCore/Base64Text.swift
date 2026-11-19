import Foundation

/// Tolerant Base64 (standard or URL-safe, padded or not, embedded whitespace) to strict UTF-8.
/// Port of `JackiVpn.Subscriptions.Parsing.Base64Text`.
public enum Base64Text {
    public static func decodeToString(_ input: String, maxChars: Int = 4 * 1024 * 1024) -> String? {
        guard !input.isEmpty, input.count <= maxChars, let data = decode(input) else { return nil }
        guard let text = String(data: data, encoding: .utf8) else { return nil }
        // Reject binary garbage that happens to be valid UTF-8.
        var control = 0
        for scalar in text.unicodeScalars where scalar.properties.generalCategory == .control {
            if scalar != "\r" && scalar != "\n" && scalar != "\t" { control += 1 }
        }
        return control == 0 || control < text.unicodeScalars.count / 100 ? text : nil
    }

    public static func decode(_ input: String) -> Data? {
        var s = ""
        s.reserveCapacity(input.utf8.count + 3)
        for c in input.unicodeScalars {
            switch c {
            case "-": s.append("+")
            case "_": s.append("/")
            case " ", "\r", "\n", "\t": continue
            case "A"..."Z", "a"..."z", "0"..."9", "+", "/", "=": s.unicodeScalars.append(c)
            default: return nil
            }
        }
        while let last = s.last, last == "=" { s.removeLast() }
        if s.isEmpty || s.count % 4 == 1 { return nil }
        s += String(repeating: "=", count: (4 - s.count % 4) % 4)
        return Data(base64Encoded: s)
    }

    public static func looksLikeBase64(_ text: String) -> Bool {
        let sample = text.prefix(4096)
        guard !sample.isEmpty, !sample.contains("://") else { return false }
        return sample.unicodeScalars.allSatisfy { c in
            switch c {
            case "A"..."Z", "a"..."z", "0"..."9", "+", "/", "=", "-", "_", "\r", "\n", " ": return true
            default: return false
            }
        }
    }
}
