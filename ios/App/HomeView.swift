import SwiftUI

struct HomeView: View {
    var body: some View {
        ZStack {
            Color.black.ignoresSafeArea()
            VStack(alignment: .leading, spacing: 22) {
                HStack {
                    Text("LROUTE")
                        .font(.system(size: 32, weight: .light))
                        .tracking(3)
                        .foregroundStyle(.white)
                    Spacer()
                    Text("L")
                        .font(.system(size: 28, weight: .medium))
                        .foregroundStyle(.black)
                        .frame(width: 42, height: 42)
                        .background(.white, in: RoundedRectangle(cornerRadius: 12))
                }
                Text("VPN CLIENT")
                    .font(.system(size: 11, weight: .medium))
                    .tracking(2)
                    .foregroundStyle(.gray)
                VStack(alignment: .leading, spacing: 12) {
                    Text("TUNNEL STATUS")
                        .font(.system(size: 12, weight: .medium))
                        .tracking(1.5)
                        .foregroundStyle(.gray)
                    Text("ENGINE SETUP PENDING")
                        .font(.system(size: 23))
                        .foregroundStyle(.white)
                    Text("LRoute iOS foundation is ready. VPN engine integration is the next milestone.")
                        .font(.system(size: 14))
                        .foregroundStyle(.gray)
                        .fixedSize(horizontal: false, vertical: true)
                }
                .padding(20)
                .frame(maxWidth: .infinity, alignment: .leading)
                .background(Color(white: 0.09), in: RoundedRectangle(cornerRadius: 22))
                VStack(spacing: 10) {
                    Text("SERVER PROFILES")
                        .font(.system(size: 12, weight: .medium))
                        .tracking(1.5)
                        .foregroundStyle(.gray)
                    Text("Profile import is coming in the next milestone.")
                        .font(.system(size: 14))
                        .foregroundStyle(.white.opacity(0.8))
                }
                .frame(maxWidth: .infinity)
                .padding(20)
                .background(Color(white: 0.09), in: RoundedRectangle(cornerRadius: 22))
                Spacer()
                Text("LRoute iOS · Development Build")
                    .font(.system(size: 11))
                    .foregroundStyle(.gray)
                    .frame(maxWidth: .infinity)
            }
            .padding(24)
        }
    }
}
