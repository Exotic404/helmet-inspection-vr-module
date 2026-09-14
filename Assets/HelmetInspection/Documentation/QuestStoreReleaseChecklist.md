# Meta Quest store release checklist

The development build is intentionally unsigned with a private production identity. Before submission:

- Replace the development signing identity with the publisher-owned Android keystore and back it up securely outside Git.
- Confirm the final package ID, app name, version name, monotonically increasing version code, organization, and entitlement strategy.
- Build the release AAB from **Helmet Inspection > Build Quest Store AAB** with Development Build and script debugging disabled.
- Run the automated module validator and archive its report with the release candidate.
- Test first launch, tracking loss/recovery, recenter, boundary behavior, both Touch Plus controllers, grab/return sockets, scanner activation, all ten findings, reset, completion, suspend/resume, and uninstall/reinstall on a retail Quest 3.
- Profile sustained frame rate, CPU/GPU frame timing, thermals, memory, and draw calls on-device. Do not judge performance from desktop Play Mode alone.
- Complete Meta's current data-use, privacy policy, comfort, age rating, accessibility, support/contact, store artwork, trailer, and content declarations in the release dashboard.
- Review third-party model, music, texture, font, and code licenses and retain evidence for every shipped asset.
- Have a qualified subject-matter expert approve the defect descriptions and safety language. This training module must not imply certified pass/fail authority unless the underlying process is formally validated.
