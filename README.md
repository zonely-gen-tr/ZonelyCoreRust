ZonelyCoreRust
A step by step setup guide to install the ZonelyCoreRust plugin on your Rust server and send commands from your website.


Before integrating your website with ZonelyCoreRust, ensure you are using the latest official version. You can download the newest ZonelyCoreRust release here:  
https://zonely.gen.tr/plugins/zonelycorerust-1-0-0.zip

## 1) Prerequisites

- A running **Rust** server with **uMod/Oxide** installed.

## 2) Install the plugin on the server

1. Copy the **ZonelyCoreRust.cs** file to:  
   `oxide/plugins/ZonelyCoreRust.cs`
2. Start the server or reload the plugin:

   ```
   oxide.reload ZonelyCoreRust
   ```
3. After the first run, a configuration file will be created:  
   `oxide/config/ZonelyCoreRust.json`
4. Open the file and set the **Server Token** value:

```json
{
  "serverToken": "YOUR_PASSWORD"
}
```

## 3) Create a server record in the panel

1. Go to the **Servers** page.
2. Click **Add Server**.
3. Fill in the fields:
   - **Plugin**: `ZonelyCoreRust`
   - **Server Token**: From `ZonelyCoreRust.json` → `serverToken`
   - (Rust does not require **IP/Port**)
4. Click **Check Plugin Connection**.
5. If successful, click **Publish** to save.

<Warning>
  The **Server Token** in the panel must exactly match the `serverToken` in `ZonelyCoreRust.json`.
</Warning>

## 4) Troubleshooting

- **Unauthorized or invalid token**: Do the `serverToken` and panel **Server Token** match?
- **Plugin not visible or not loading**: Is the path correct (`oxide/plugins/ZonelyCoreRust.cs`)?  
  After running `oxide.reload ZonelyCoreRust`, do you see any console errors?
- **Connection failed**: Is the plugin active and the correct plugin type (**ZonelyCoreRust**) selected in the panel?

## 5) Security tips

- Keep the **Server Token** known only to authorized personnel.
- Rotate the token regularly (update both `ZonelyCoreRust.json` and the panel record).
- Serve your panel over **HTTPS**.

## 6) FAQ

**Why are IP and port not required?**  
This setup only needs the **Server Token**.

**Can I add more than one Rust server?**  
Yes. Create a unique **Server Token** for each server and add separate records.

**What should I do if the token is leaked?**  
Generate a new token and update both `ZonelyCoreRust.json` and the panel entry.

**I get “Connection Failed.” What should I check first?**  
Ensure the plugin is installed, the `serverToken` matches the panel value, and no errors appear after `oxide.reload ZonelyCoreRust`.

<Check>
  Setup complete. You can now send commands to your Rust server safely through the panel.
</Check>
