using Trinix.Bundle;

namespace Trinix.Conformance;

/// <summary>
///     The permission array, and the prose beside it.
/// </summary>
/// <remarks>
///     <para>
///         Doc 01's promise for this section is that doctor says it <i>before</i> the
///         packager and the launcher do. An unknown permission is already a refusal at
///         seal and a refusal at launch — <see cref="PermissionSet.Parse(BundleInfo)" />
///         throws, and doc 04 explains at length why dropping the string or
///         approximating it are both worse — so the only thing left to improve is
///         when the developer hears about it, and the answer should be "at their own
///         terminal, with the fix in the same line".
///     </para>
///     <para>
///         The rest is the Store's half of doc 04 § Consent, which is the one place a
///         conformance tool can act on a distribution policy: a permission the
///         repository will never grant, and a permission declared without the sentence
///         the consent dialog is supposed to show. Both are warnings, and both carry
///         <see cref="Finding.StoreGate" /> so that doc 09's
///         <c>trinix doctor --store</c> reports them as the refusals they will be.
///     </para>
/// </remarks>
static class PermissionChecks {
    internal static void Run(DoctorContext context, BundleInfo info) {
        var known = Known(context, info);
        Duplicates(context, info);
        StorePolicy(context, known);
        UsageDescriptions(context, info, known);
        Display(context, known);
    }

    static PermissionSet Known(DoctorContext context, BundleInfo info) {
        context.Ran("permissions.known");

        if (PermissionSet.TryParse(info.Permissions, out var parsed, out var unknown)) {
            return parsed;
        }

        foreach (var name in unknown) {
            context.Error(
                "permissions.known",
                $"'{name}' is not a permission Trinix defines",
                "the sealer refuses to sign it and the launcher refuses to start it — an unknown "
                + "permission is neither dropped nor approximated, because the first runs the "
                + "application with less authority than its consent screen described and the second "
                + "grants authority nobody wrote down",
                $"use one of: {string.Join(", ", BundlePermissions.Known)}. If you meant something "
                + "that is not in that list, it is not a permission — see doc 04: authority an "
                + "application has by being a process is not declared",
                BundleLayout.InfoPath
            );
        }

        return parsed;
    }

    static void Duplicates(DoctorContext context, BundleInfo info) {
        context.Ran("permissions.duplicate");

        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (var name in info.Permissions) {
            if (seen.Add(name)) {
                continue;
            }

            context.Warn(
                "permissions.duplicate",
                $"'{name}' is declared more than once",
                "the permission set is a set, so the repeat changes nothing about what the "
                + "application may do — which is exactly why it is worth reporting: it means this "
                + "file was edited by hand or merged, and the next thing in it may not be harmless",
                $"remove the duplicate \"{name}\"",
                BundleLayout.InfoPath
            );
        }
    }

    static void StorePolicy(DoctorContext context, PermissionSet permissions) {
        context.Ran("permissions.store-reserved");

        foreach (var permission in permissions.Enumerate()) {
            if (!BundlePermissions.NeverGrantedByTheStore(permission)) {
                continue;
            }

            var name = BundlePermissions.NameOf(permission);
            context.Warn(
                "permissions.store-reserved",
                $"'{name}' is reserved and is never granted by the Store",
                "doc 04 keeps it in the vocabulary for accessibility and remote-control tools, "
                + "granted by the user in Settings and by nobody else — so a bundle that declares it "
                + "installs, runs, and is refused at submission",
                $"remove \"{name}\" unless this application is an accessibility or remote-control "
                + "tool distributed outside the repository; there is no narrower permission to ask "
                + "for instead, which is the reason this one exists",
                BundleLayout.InfoPath,
                storeGate: true
            );
        }
    }

    static void UsageDescriptions(DoctorContext context, BundleInfo info, PermissionSet permissions) {
        context.Ran("permissions.usage.present");

        foreach (var permission in permissions.Enumerate()) {
            var name = BundlePermissions.NameOf(permission);
            var shown = BundlePermissions.UserVisibleName(permission);

            if (shown is null) {
                // Never shown to a user, so there is no dialog for a sentence to appear in.
                continue;
            }

            if (info.UsageDescriptions.TryGetValue(name, out var description)
                && !string.IsNullOrWhiteSpace(description)) {
                continue;
            }

            context.Warn(
                "permissions.usage.present",
                $"'{name}' has no usageDescription",
                $"the consent dialog will say \"{shown}\" and nothing about why — and doc 04 makes the "
                + "durable grant the unmarked default, so this is the one sentence standing between "
                + "the user and a permanent yes or a permanent no",
                $"add \"usageDescriptions\": {{ \"{name}\": \"…\" }} to {BundleLayout.InfoPath}, "
                + "saying what the application does with it in the user's terms",
                BundleLayout.InfoPath,
                storeGate: true
            );
        }

        context.Ran("permissions.usage.undeclared");
        foreach (var (key, _) in info.UsageDescriptions.OrderBy(pair => pair.Key, StringComparer.Ordinal)) {
            if (!BundlePermissions.TryParse(key, out var flag)) {
                context.Warn(
                    "permissions.usage.undeclared",
                    $"usageDescriptions has an entry for '{key}', which is not a permission",
                    "nothing will ever show it: the consent dialog looks the description up by the "
                    + "permission it is about, and there is no permission by this name",
                    $"correct the key, or remove it. The permissions are: {string.Join(", ", BundlePermissions.Known)}",
                    BundleLayout.InfoPath
                );

                continue;
            }

            if (permissions.Has(flag)) {
                if (BundlePermissions.UserVisibleName(flag) is null) {
                    context.Warn(
                        "permissions.usage.undeclared",
                        $"usageDescriptions has an entry for '{key}', which is never shown to a user",
                        "doc 04 marks it \"never shown\" — the consent dialog for it is one every user "
                        + "answers the same way, so it is not asked and the sentence has no reader",
                        $"remove the \"{key}\" entry",
                        BundleLayout.InfoPath
                    );
                }

                continue;
            }

            context.Warn(
                "permissions.usage.undeclared",
                $"usageDescriptions explains '{key}', which this bundle does not declare",
                "either the permission was removed and its description was left behind, or it was "
                + "meant to be declared and is not — and the second is an application that will be "
                + "refused at the moment it asks",
                $"declare \"{key}\" in \"permissions\", or remove its description",
                BundleLayout.InfoPath
            );
        }
    }

    static void Display(DoctorContext context, PermissionSet permissions) {
        context.Ran("permissions.display");

        if (permissions.Has(Permissions.Display)) {
            return;
        }

        context.Note(
            "permissions.display",
            "no 'display' permission is declared",
            "the unit will bind no Wayland socket and set no WAYLAND_DISPLAY, so this bundle can "
            + "never open a window — correct for a command-line tool or a background service, and a "
            + "silent failure to start for anything else",
            "if this application has a window, add \"display\" to \"permissions\"",
            BundleLayout.InfoPath
        );
    }
}
