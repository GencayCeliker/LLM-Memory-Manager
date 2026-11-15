# 🧠 LLM Memory Manager

![C#](https://img.shields.io/badge/C%23-239120?style=for-the-badge&logo=dotnet&logoColor=white)
![.NET 10](https://img.shields.io/badge/.NET-10-512BD4?style=for-the-badge&logo=dotnet&logoColor=white)
![WPF](https://img.shields.io/badge/UI-WPF-0078D7?style=for-the-badge)
![EF Core](https://img.shields.io/badge/EF%20Core-10-6C1D5F?style=for-the-badge)
![SQL Server](https://img.shields.io/badge/SQL%20Server-CC2927?style=for-the-badge)

Bilgisayarda yerel olarak çalışan bir dil modeliyle (**LM Studio** üzerinden Gemma) sohbet eden ve sohbet geçmişini **SQL Server**'da saklayan **WPF / .NET 10** masaüstü uygulaması. Uzun sohbetlerde modele gönderilecek bağlamı (context) mesaj ve token sınırıyla yöneterek düşük VRAM'li ekran kartlarında da kullanılabilmesini hedefler.

---

## 🚀 Özellikler

- **Sohbet arayüzü:** Oturum (sohbet) listesi, yeni sohbet başlatma, mesaj gönderme
- **Kalıcı hafıza:** Kullanıcılar, oturumlar ve tüm mesajlar SQL Server'da tutulur (EF Core)
- **Bağlam yönetimi:** Modele yalnızca son *N* mesaj gönderilir; her mesajın yaklaşık **token sayısı** saklanır
- **Model ayarları penceresi:** Model adı, context length, hafızadaki mesaj sayısı, CPU thread, GPU layer ve temperature. Ayarlar veritabanında kalıcı olarak saklanır.
- **Tanılama ve ölçüm penceresi:** Uygulamanın bellek kullanımı (working set), LM Studio bağlantı durumu, GPU belleği ve seçilip kopyalanabilen tanılama günlüğü
- **WhatsApp webhook (deneysel):** Yerel bir HTTP dinleyicisi (`localhost:5000`) gelen mesajı kaydeder, modelden yanıt alır ve harici bir Node.js WhatsApp köprüsüne (`localhost:3000`) iletir

## 🏗️ Mimari

```
LLM Memory Manager/
├── MainWindow.xaml(.cs)        ← Sohbet ekranı
├── SettingsWindow.xaml(.cs)    ← Model ve bellek ayarları
├── DiagnosticsWindow.xaml(.cs) ← Bellek / GPU / bağlantı ölçümleri
├── Database/
│   ├── AppDbContext.cs         ← EF Core DbContext (tablo eşlemeleri)
│   └── ChatRepository.cs       ← Kullanıcı, oturum ve mesaj işlemleri (async)
├── Models/                     ← User, Session, ChatMessage, AppSettings
└── Services/
    ├── LmStudioService.cs      ← LM Studio OpenAI uyumlu API istemcisi (127.0.0.1:1234)
    └── WhatsappWebhookService.cs
```

## 🗄️ Veritabanı (`LLM`)

`Veri Tabanı/LLM.sql` ile oluşturulan 4 tablo: `T_User`, `T_Session`, `T_Chat` (mesaj + token sayısı), `T_Settings` (model parametreleri). EF Core, `OnModelCreating` içinde bu tablolara eşlenir.

---

## ⚙️ Kurulum

**Gereksinimler:**
- Visual Studio 2026 (veya .NET 10 SDK destekleyen Visual Studio 2022) – *.NET masaüstü geliştirme* iş yükü
- SQL Server (Express yeterli) ve SQL Server Management Studio (SSMS) 22
- [LM Studio](https://lmstudio.ai/) – bir model yükleyip **Local Server**'ı `1234` portunda başlatın

1. **İndirin**
   ```bash
   git clone https://github.com/GencayCeliker/LLM-Memory-Manager.git
   ```
2. **Veritabanını kurun (SSMS 22):** `Veri Tabanı/LLM.sql` dosyasını açıp **Execute (F5)**'e basın.
3. **Bağlantıyı kontrol edin:** `LLM Memory Manager/Database/AppDbContext.cs` içinde `Server=.\SQLEXPRESS;Database=LLM` kullanılır; farklı bir sunucu kullanıyorsanız burayı değiştirin.
4. **Çalıştırın:** `LLM Memory Manager.slnx` dosyasını açın ve **F5** ile başlatın. LM Studio'da modelin yüklü ve sunucunun açık olduğundan emin olun.

> WhatsApp entegrasyonu için ayrıca bir Node.js WhatsApp köprüsü gerekir; bu depoda yer almaz. Uygulamanın geri kalanı onsuz çalışır.
