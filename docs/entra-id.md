# Sign in with Entra ID

Optional. When this is set up, the sign-in page offers **Sign in with
Microsoft**. A person's first sign-in creates their portal account, and each
later one updates their name, email, role and linked students from Entra.
Changes in Entra reach the portal within 8 hours.

Local accounts keep working alongside it unless the owner turns them off in
**Settings → Sign-in**. The owner always signs in with a password, at
`/Login?local=true` when local sign-in is off.

## Set up the app registration

Each school uses its own app registration in its own tenant. In the Microsoft
Entra admin center:

1. **App registrations → New registration.** Choose **Accounts in this
   organizational directory only**, and add a **Web** redirect URI
   `https://<portal>/signin-oidc`. Then, under **Authentication**, add a second
   redirect URI `https://<portal>/signout-callback-oidc`. Note the application
   (client) ID and directory (tenant) ID.

2. **Certificates & secrets → New client secret.** Note its value and expiry
   date. Microsoft sign-in stops working if the secret expires, so replace it
   in `.env` before then.

3. **Token configuration → Add optional claim → ID → `email`.** Without it the
   portal uses the sign-in name as the email.

4. **App roles → Create app role**, twice, for **Users/Groups**: one with the
   value `Admin` and one with the value `Parent`. Values are case-sensitive.

5. **Enterprise applications →** your app **→ Properties:** set **Assignment
   required** to **Yes**. Then, under **Users and groups**, assign IT to
   **Admin** and parents to **Parent**. Assigning groups needs Entra ID P1 or
   P2; without it, assign people individually.

6. **Add the `students` claim to the ID token.** It holds each parent's student
   usernames, comma-separated (`jsmith26,asmith29`). Use whichever source
   suits you:

   - **An on-premises AD attribute synced by Entra Connect:** under Enterprise
     applications → your app → **Single sign-on → Attributes & Claims → Add new
     claim**, use the name `students` and a source attribute such as
     `user.extensionattribute1`. Then set `acceptMappedClaims` to `true` in the
     app registration's **Manifest**.
   - **A directory extension registered on the app:** under **Token
     configuration → Add optional claim → ID**, pick the extension. Then set
     `LULL_ENTRA_STUDENTS_CLAIM` to its full name
     (`extension_<app id>_<name>`).

7. **Configure the portal.** In `.env`, set `LULL_ENTRA_TENANT_ID`,
   `LULL_ENTRA_CLIENT_ID` and `LULL_ENTRA_CLIENT_SECRET`, then run
   `docker compose up -d`. The server must be able to reach
   `login.microsoftonline.com`.

If you named the roles or the claim differently, see the `LULL_ENTRA_*`
settings in [`.env.example`](../.env.example).

## Move existing local accounts to Entra

By default, a Microsoft sign-in is refused if its email already belongs to a
local account. To move people over:

1. If the owner's email is also someone's Microsoft account, change the owner
   to a separate address, such as a shared IT mailbox. The owner account is
   never taken over.
2. In **Settings → Sign-in**, turn on **A first Microsoft sign-in takes over
   the local account with the same email**.
3. Each person's first Microsoft sign-in then takes over their account and
   keeps its history. Their password stops working.
4. Once everyone has moved, turn the setting off. You can also turn off local
   sign-in at this point.

## Troubleshooting

| Message | Cause and fix |
|---|---|
| "Your Microsoft account isn't set up to use the portal" | The person isn't assigned to **Admin** or **Parent** on the enterprise application |
| "Your email already belongs to another portal account" | A local or older Entra account has that email. Delete it under **Users** if it should go, or move it as above |
| A parent can't see any Macs | Their `students` claim is missing or wrong. Invalid usernames are left out and logged in the portal's log |
