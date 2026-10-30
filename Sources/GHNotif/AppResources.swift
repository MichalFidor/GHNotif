import Foundation

/// Some SwiftPM toolchains generate a `Bundle.module` accessor that only looks next to the .app,
/// which crashes a signed app whose resource bundle lives in Contents/Resources.
enum AppResources {
    static let bundle: Bundle = {
        let name = "GHNotif_GHNotif.bundle"
        for base in [Bundle.main.resourceURL, Bundle.main.bundleURL].compactMap({ $0 }) {
            if let bundle = Bundle(url: base.appendingPathComponent(name)) { return bundle }
        }
        return Bundle.module
    }()
}
