## MailSuite (.NET 8)

Suite composta da 2 programmi:
- MailFetcher (console): scarica email via IMAP e salva `.eml` replicando la struttura cartelle per più caselle.
- MailViewer (web): interfaccia per consultare gli `.eml` come client email.

### Requisiti
- .NET SDK 8

### Configurazione
`MailFetcher/appsettings.json` esempio:

```json
{
  "OutputRoot": "Data",
  "Accounts": [
    {
      "Name": "Example",
      "Host": "imap.example.com",
      "Port": 993,
      "UseSsl": true,
      "Username": "user@example.com",
      "Password": "changeme"
    }
  ]
}
```

`MailViewer/appsettings.json`:

```json
{ "DataRoot": "../MailFetcher/Data" }
```

Puoi usare `appsettings.Local.json` per credenziali locali non versionate.

### Esecuzione
1. Compila: `dotnet build MailSuite.sln -c Release`
2. Avvia fetcher: `dotnet run --project MailFetcher`
   - Scarica le email per ogni account in `MailFetcher/Data/<Account>/<Cartella>/*.eml`.
3. Avvia viewer: `dotnet run --project MailViewer`
   - Apri `http://localhost:5000` (o la porta mostrata) e naviga tra caselle/cartelle/messaggi. Download allegati supportato.

### Admin UI e Sicurezza
- Basic Auth per `/admin.html` e `/api/config` (MailViewer):
  - Variabili ambiente supportate: `MAILVIEWER_AdminAuth__Username`, `MAILVIEWER_AdminAuth__Password`.
  - In produzione l'admin è disabilitato per default. Per abilitarlo: `MAILVIEWER_AdminEnabled=true`.

### Note
- Il fetch salva solo nuovi UID se l'`.eml` esiste già.
- Supporto struttura cartelle IMAP completa e `INBOX`.
- Compatibile Windows/macOS/Linux (cross-platform .NET 8). 


