import SwiftUI
import UniformTypeIdentifiers

struct HomeView: View {
    @StateObject private var vpn = VPNManager()
    @State private var showingImporter = false
    @State private var profileToDelete: VPNProfile?
    @State private var showingDeleteConfirmation = false

    private var confType: UTType { UTType(filenameExtension: "conf") ?? .plainText }

    var body: some View {
        ZStack {
            Color.black.ignoresSafeArea()
            VStack(alignment: .leading, spacing: 22) {
                header
                Text("VPN CLIENT").font(.system(size: 11, weight: .medium)).tracking(2).foregroundStyle(.gray)
                VStack(alignment: .leading, spacing: 14) {
                    Text("TUNNEL STATUS").font(.system(size: 12, weight: .medium)).tracking(1.5).foregroundStyle(.gray)
                    HStack(spacing: 10) {
                        Circle().fill(vpn.isConnected ? .green : .gray).frame(width: 9, height: 9)
                        Text(vpn.statusText).font(.system(size: 22, weight: .medium)).foregroundStyle(.white)
                    }
                    Button {
                        if vpn.isConnected { vpn.disconnect() } else { Task { await vpn.connect() } }
                    } label: {
                        Text(vpn.isConnected ? "DISCONNECT" : "CONNECT")
                            .font(.system(size: 15, weight: .semibold)).tracking(1.5).foregroundStyle(.black)
                            .frame(maxWidth: .infinity).padding(.vertical, 17)
                            .background(.white, in: RoundedRectangle(cornerRadius: 14))
                    }
                    .disabled(!vpn.isConfigured && !vpn.isConnected)
                    .opacity(vpn.isConfigured || vpn.isConnected ? 1 : 0.45)
                }
                .padding(20).frame(maxWidth: .infinity, alignment: .leading)
                .background(Color(white: 0.09), in: RoundedRectangle(cornerRadius: 22))

                VStack(alignment: .leading, spacing: 14) {
                    HStack {
                        Text("SERVER PROFILES").font(.system(size: 12, weight: .medium)).tracking(1.5).foregroundStyle(.gray)
                        Spacer()
                        Text("\(vpn.profiles.count) SAVED").font(.system(size: 10, weight: .medium)).foregroundStyle(.gray)
                    }
                    if vpn.profiles.isEmpty {
                        Text("No profiles imported yet.").font(.system(size: 13)).foregroundStyle(.gray).padding(.vertical, 8)
                    } else {
                        ForEach(vpn.profiles) { profile in
                            HStack(spacing: 12) {
                                Button { vpn.selectProfile(profile.id) } label: {
                                    HStack(spacing: 10) {
                                        Image(systemName: vpn.selectedProfileID == profile.id ? "largecircle.fill.circle" : "circle")
                                            .foregroundStyle(vpn.selectedProfileID == profile.id ? .white : .gray)
                                        VStack(alignment: .leading, spacing: 4) {
                                            Text(profile.name).font(.system(size: 15, weight: .medium)).foregroundStyle(.white).lineLimit(1)
                                            Text(profile.status == .connected ? "CONNECTED" : "WIREGUARD PROFILE")
                                                .font(.system(size: 9, weight: .medium)).tracking(1)
                                                .foregroundStyle(profile.status == .connected ? .green : .gray)
                                        }
                                        Spacer(minLength: 4)
                                    }.contentShape(Rectangle())
                                }.buttonStyle(.plain)
                                Button(role: .destructive) {
                                    profileToDelete = profile
                                    showingDeleteConfirmation = true
                                } label: {
                                    Image(systemName: "trash").font(.system(size: 13)).foregroundStyle(.gray).padding(8)
                                }.buttonStyle(.plain)
                            }.padding(.vertical, 5)
                        }
                    }
                    Button { showingImporter = true } label: {
                        HStack { Image(systemName: "plus"); Text("IMPORT WIREGUARD CONFIG") }
                            .font(.system(size: 13, weight: .semibold)).foregroundStyle(.white)
                            .frame(maxWidth: .infinity).padding(.vertical, 15)
                            .overlay(RoundedRectangle(cornerRadius: 14).stroke(.white.opacity(0.35)))
                    }
                }
                .padding(20).frame(maxWidth: .infinity, alignment: .leading)
                .background(Color(white: 0.09), in: RoundedRectangle(cornerRadius: 22))
                Spacer(minLength: 4)
                Text("LRoute iOS · WireGuard").font(.system(size: 11)).foregroundStyle(.gray).frame(maxWidth: .infinity)
            }.padding(24)
        }
        .fileImporter(isPresented: $showingImporter, allowedContentTypes: [confType, .plainText], allowsMultipleSelection: false) { result in
            switch result {
            case .success(let urls):
                guard let url = urls.first else { return }
                let access = url.startAccessingSecurityScopedResource()
                defer { if access { url.stopAccessingSecurityScopedResource() } }
                do {
                    let data = try Data(contentsOf: url)
                    guard let config = String(data: data, encoding: .utf8) else {
                        vpn.errorMessage = "The selected file is not valid UTF-8 text."
                        return
                    }
                    let name = url.deletingPathExtension().lastPathComponent
                    Task { await vpn.importProfile(named: name, config: config) }
                } catch { vpn.errorMessage = "Could not read file: \(error.localizedDescription)" }
            case .failure(let error): vpn.errorMessage = error.localizedDescription
            }
        }
        .confirmationDialog("Delete \(profileToDelete?.name ?? "profile")?", isPresented: $showingDeleteConfirmation, titleVisibility: .visible) {
            Button("Delete Profile", role: .destructive) {
                if let profileToDelete { Task { await vpn.deleteProfile(profileToDelete.id) } }
            }
            Button("Cancel", role: .cancel) { profileToDelete = nil }
        } message: {
            Text("This removes the saved VPN profile from this device.")
        }
        .alert("LRoute", isPresented: Binding(get: { vpn.errorMessage != nil }, set: { if !$0 { vpn.errorMessage = nil } })) {
            Button("OK", role: .cancel) { vpn.errorMessage = nil }
        } message: { Text(vpn.errorMessage ?? "") }
        .task { await vpn.reload() }
    }

    private var header: some View {
        HStack {
            Text("LROUTE").font(.system(size: 32, weight: .light)).tracking(3).foregroundStyle(.white)
            Spacer()
            Text("L").font(.system(size: 28, weight: .medium)).foregroundStyle(.black)
                .frame(width: 42, height: 42).background(.white, in: RoundedRectangle(cornerRadius: 12))
        }
    }
}
