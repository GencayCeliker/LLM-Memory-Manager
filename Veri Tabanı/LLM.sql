CREATE DATABASE LLM;
GO
USE LLM;
GO
-- Kullanıcı bilgilerini tutan tablo
CREATE TABLE T_User
(
User_ID INT IDENTITY PRIMARY KEY,
Phone_Number NVARCHAR(15) UNIQUE NOT NULL,
User_Name NVARCHAR(50),
Created_At DATETIME DEFAULT GETDATE()
);
GO
-- Sohbet oturumlarını (seansları) yöneten tablo
CREATE TABLE T_Session
(
Session_ID INT IDENTITY PRIMARY KEY,
User_ID INT,
Session_Start DATETIME DEFAULT GETDATE(),
Session_End DATETIME,
FOREIGN KEY (User_ID) REFERENCES T_User(User_ID)
);
GO
-- Mesaj geçmişini ve VRAM optimizasyonu için kritik olan Token sayısını tutan tablo
CREATE TABLE T_Chat
(
Message_ID INT IDENTITY PRIMARY KEY,
Session_ID INT,
User_ID INT,
Role NVARCHAR(25), -- 'user' veya 'assistant'
Message_Content NVARCHAR(MAX),
Token_Count INT, -- Modele gönderilecek context boyutunu hesaplamak için eklendi
Created_At DATETIME DEFAULT GETDATE(),
FOREIGN KEY (Session_ID) REFERENCES T_Session(Session_ID),
FOREIGN KEY (User_ID) REFERENCES T_User(User_ID)
);
GO
-- Uygulama arayüzünden değiştirilecek LLM parametrelerini kalıcı tutan tablo
CREATE TABLE T_Settings
(
Setting_ID INT IDENTITY PRIMARY KEY,
Max_Memory_Messages INT DEFAULT 10, -- RAM'de anlık tutulacak mesaj sınırı
Context_Length INT DEFAULT 8192,     -- Modelin maksimum context uzunluğu
Cpu_Threads INT DEFAULT 8,           -- Ayrılacak CPU thread sayısı
Gpu_Layers INT DEFAULT 32,           -- GPU'ya yüklenecek katman sayısı
Temperature FLOAT DEFAULT 0.7,       -- Yaratıcılık oranı
Model_Name NVARCHAR(100) DEFAULT 'Gemma4' -- Aktif kullanılan model adı
);
GO