# Known limitations

The single list of current limitations, and the source for the README's limitations section. Update it whenever a
feature is finished or a limitation is fixed, and remove entries that no longer apply. Every group except the last
describes how the app behaves. "Not yet tested" is what still needs checking, and should be emptied before release.

Last updated 2026-11-06.

**Site maps and alignment**
- One alignment moves a whole drawing, so items in a building drawn elsewhere must be moved by hand on that plan.
- Room labels can't be moved. Items that an alignment puts off the plan can't be reached there.
- Dense maps have overlapping labels until you zoom in.
- Undo for moved items only lasts until Move items is switched off or the page is left.

**Network layer import**
- The Wi-Fi map reader expects red names ending in a three-digit model, and symbols in set colors. Maps drawn differently need its colors adjusted in the code.
- Marker types are fixed: indoor, outdoor and alternate outdoor access points, MDF and IDF.
- OCR makes mistakes, and not all of them are flagged, so spot-check important maps.

**Photos and notes**
- HEIC files are refused with a message asking for a JPEG. Many iPhones convert when uploading through a browser.
- Uploads are limited to 25 MB and 100 megapixels each, and saved photos to 2400 pixels on the long side.
- Photos are stored in the data folder, so backups grow with them. There's no storage limit or usage report.
- Photos save as soon as they're chosen, so Cancel in the dialog doesn't undo them. They're deleted one at a time.
- A caption applies to every photo in that upload, and can't be edited afterward.

**Links and PDF export**
- A link reopens the map as it is now, not a saved copy, so moved or deleted items will have changed.
- Links work only for people with an account. Outside sharing isn't built.
- A saved PDF is outside the app's permissions. It carries a notice and the export is audited, but nothing stops it being passed on.
- PDFs use the standard PDF fonts: accents are dropped ("Peña" prints as "Pena") and other non-English characters become `?`.
- PDFs are one 11×17 page. Photos and notes aren't included, only the pin and its name.
- The drawing is an image of up to 4800 pixels on its long side, so it can blur at high zoom. Text and symbols placed on it stay sharp.
- PDFs have no tagged structure for screen readers, apart from a document title.

**Custom layers**
- Items are single points with a name. Lines and shaded areas aren't built.
- Layers are shared by role. Private layers aren't built.
- New layers start as Techs and above, and only a Manager or Admin can change that.
- An item can't be switched to a different legend entry: delete it and add it again. A legend entry in use can't be removed until its items are deleted.
- Shapes and colors come from fixed lists of 5 and 7.
- A layer on a Wi-Fi map shows on linked plans, but can only be edited on its own map.

**Orders**
- Each order is one item with a quantity. Several different items mean several orders.
- Stage names are fixed in the code (`OrderStages`), and can't be changed from the app.
- Orders are for Techs and above. The staff member an order is for can't see its progress.
- There are no email or other notifications: the app is standalone, so people check the Orders page.
- Orders can be cancelled but not deleted, and sites with orders can't be deleted.
- Contacts are names, not user accounts, so they don't sign in or get told about an order. Renaming someone on a staff list doesn't change existing orders.
- A site has one usual contact. Other people can be picked or typed on each order.
- Staff lists aren't tied to particular techs: any Tech or above can edit any site's list. There's no bulk import yet, so each person is added by hand.
- No costs or budgets are tracked. The quote or PO number points to where those are kept.
- A pin's name shows when zoomed in or on hover, so its stage isn't readable at full-map zoom without opening it.
- An order has one pin, on one map. It shows on plans linked to that map, but not on other maps of the site.
- Dates use the server's time zone, so set `TZ` on the Docker host.

**Repairs**
- The asset system isn't updated automatically. The app tracks what it should say, and someone makes the change there and marks it as updated.
- The status names it should show (`RepairStatuses.InventoryName`) are fixed in the code, and may not match the wording of your system.
- The downloaded list has one asset tag per line, which assumes your system accepts that for a bulk update.
- No student details are stored, so the app can't show who had a device or who a fine is for.
- An asset tag can't be corrected after check-in. A mistyped one creates a new device, which stays on record.
- A serial number can only belong to one device, so a re-tagged device needs its old record's serial number changed first.
- Models and reasons are listed in the order they were added, and can't be reordered.
- At 1400 pixels wide with the side menu open, the repairs table scrolls sideways, which hides its last columns until scrolled.
- A repair is one reason. Parts used go in the notes, and aren't tracked or counted.
- Repairs can't be deleted, and sites with repairs can't be deleted.
- The existing spreadsheet can't be imported yet.
- Camera scanning is experimental, and hasn't been tried on real tags and phones, so people are asked to check each tag after scanning.
- Live camera scanning needs HTTPS the browser accepts, and a phone needs access to the internal network. Taking a photo is the fallback where live video isn't allowed.
- Without the asset tag length set, the scanner accepts any barcode it reads, including a serial number.
- Thin barcodes need to fill most of the picture. In testing with look-alike photos, Code 39 wasn't read when only about 230 pixels wide. Code 128 and ITF were, down to about 150 pixels.
- A device checked in by scanning alone has no serial number or model until someone adds them.
- The device console isn't connected: someone deprovisions the device there, then marks it here. The link goes to whatever address is set, with no way to open a particular device.
- A device only goes out of circulation through a repair set to "Can't be repaired", so one retired without a repair needs checking in first.
- Parts pulled for reuse can go in the repair notes, but aren't tracked or counted. Pickups aren't recorded.
- A device that comes back gets a warning that it may need re-enrolling, but nothing tracks whether that was done.
- The Open button for the asset system goes to the address that's set, not to the device. Paste the copied asset tag into its search.
- A new database starts with no device models, so a Manager adds them under Manage → Repair lists. The test version has its own examples.
- District settings are read at startup, so changing one means restarting the app.

**Wiki**
- No live co-editing or comments. If two people edit one page, the second to publish is warned, and can publish anyway. The other version stays in History.
- Pages go one level deep inside sections, with no pages inside pages.
- Search matches whole words or parts of words as typed, with no allowance for typos.
- Images from other sites, including those pasted from Google Docs, are left out with a warning. Save them, then drag them in. Testing simulated a Google Docs paste, so real documents may behave differently.
- Pasted colors, fonts and sizes are dropped, on purpose, to keep pages consistent. Heading levels stop at two below the title.
- Images in the library can't be deleted, and aren't included in version history. An image on a restricted page is protected by its long random address, so anyone signed in who has that address can open it.
- Saved pages can't be exported, for example to PDF or Word.
- Autosave needs a connection. Words typed in the last moments before a connection drops can be lost.

**Test version**
- User accounts and Settings stay real while it's on, because they're how you sign in and switch back. Only Admins can use it, one account at a time.
- It stays on for that account on every device until turned off.
- Importing maps is turned off while it's on.
- Its dates are set when it's first created, so waiting times grow until it's reset.
- Its database and files are kept in the data folder, so backups include them.

**Overview**
- Numbers refresh once a minute while the page is open, not the moment something changes.
- Viewers only see links, sites, maps and wiki information. Repairs and orders stay hidden from them.
- It only covers what's in this app. Other services aren't connected.

**Account recovery**
- There's no "forgot password" link on the sign-in page, on purpose. Someone needs an Admin, or access to the server.
- The server command turns off two-factor sign-in and re-enables a disabled account, because it's meant for getting back in. Both are reported when it runs.


**Security and hosting**
- There's no Content-Security-Policy header, because the app's components rely on inline styles. Wiki pages are cleaned on the server when saved, and map labels are escaped when drawn, instead.
- The published Docker image is amd64 only. An arm64 build hasn't been tried.
- The container's filesystem isn't read-only, because that hasn't been tested with the app's temporary files.
- Run directly, without Caddy, the app has no HTTPS, so it should only accept connections from the same computer.

**Accessibility**
- Click-to-align needs a mouse. Room labels aren't in the text list on each map.
- A new pin can be placed without a mouse only at the middle of the view, then adjusted with Move items.
- Menus open outside the page landmarks because of how MudBlazor builds them (best practice, not a WCAG failure).

**Not yet tested**
- A real phone photo taken sideways: automated tests confirm its size and upside-down correction, not the direction of a sideways turn.
- An iPhone HEIC upload, and an actual WebP file.
- Links, PDF export, custom layers and order pins on real maps, including printing a PDF and opening one in a standalone PDF reader.
- Opening a map from an order centers on its pin: this was built, but not checked on screen.
- PDF export inside the Docker image: the self-test covers it and passes on Windows, but hasn't yet run in CI.
- Pasting a real Google Doc and a real Word document into the wiki, and using the editor with a screen reader.
- Camera scanning with real tags, on a real phone and a real webcam. Testing used a simulated camera and generated look-alike tags, and which barcode type the real tags use is unknown.
- A real barcode scanner. Testing simulated one by typing the tag and pressing Enter, and scanners can be set up differently.
- A downloaded list in a real asset system's bulk update, including whether a batch can change a device's status.
- The organization settings set through `deploy/.env` in Docker. They were tested using `district.json`.
- The production Docker setup on a real VM, including a clean install from the published image.
- The published image and its release workflow, and the Docker security options (`no-new-privileges`, dropped capabilities) outside CI.
- The recovery commands inside Docker. They were tested on a running copy of the app, not through `docker compose exec`.
- A real screen reader (NVDA or Narrator).
