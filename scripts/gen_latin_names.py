# One-shot generator for Data/Seed/Latin_names.csv.
# Source: curated Arabic-to-Latin variant table + user-provided curated Latin list.
# Output schema: names,sex (same as Arabic_names.csv).
#
# Run from repo root:
#     python scripts/gen_latin_names.py
#
# This script is committed for reproducibility but not part of the runtime app.

import re
from collections import defaultdict

VARIANTS = {
    "M": {
        "محمد": ["Mohamed", "Mohammad", "Mohammed", "Muhammad", "Muhammed", "Mohamad"],
        "أحمد": ["Ahmed", "Ahmad"],
        "علي": ["Ali", "Aly"],
        "عمر": ["Omar", "Omer", "Umar"],
        "خالد": ["Khaled", "Khalid"],
        "فيصل": ["Faisal", "Faysal"],
        "عبدالله": ["Abdullah", "Abdallah", "Abdulla"],
        "عبدالعزيز": ["Abdulaziz", "Abdelaziz", "Abdul Aziz", "Abd Alaziz"],
        "عبدالرحمن": ["Abdulrahman", "Abdelrahman", "Abdul Rahman"],
        "عبدالرحيم": ["Abdulrahim", "Abdelrahim"],
        "عبدالكريم": ["Abdulkarim", "Abdelkarim"],
        "عبدالملك": ["Abdulmalik", "Abdelmalek"],
        "عبدالمجيد": ["Abdulmajeed", "Abdulmajid"],
        "عبداللطيف": ["Abdullatif", "Abdellatif"],
        "إبراهيم": ["Ibrahim", "Ebrahim"],
        "يوسف": ["Yusuf", "Yousef", "Yousif", "Youssef"],
        "حسن": ["Hassan", "Hasan"],
        "حسين": ["Hussein", "Hussain", "Husain", "Hossain"],
        "محمود": ["Mahmoud", "Mahmud"],
        "سعد": ["Saad"],
        "سعود": ["Saud", "Saood"],
        "سعيد": ["Saeed", "Said"],
        "سلطان": ["Sultan"],
        "تركي": ["Turki"],
        "ناصر": ["Nasser", "Naser", "Nassir"],
        "بدر": ["Badr", "Bader"],
        "صالح": ["Saleh", "Salih"],
        "يحيى": ["Yahya", "Yahia"],
        "أسامة": ["Osama", "Usama", "Oussama"],
        "زياد": ["Ziyad", "Ziad"],
        "زيد": ["Zaid", "Zayd", "Zeid"],
        "أنس": ["Anas", "Anass"],
        "حمزة": ["Hamza", "Hamzah"],
        "ماجد": ["Majed", "Majid", "Maged"],
        "ياسر": ["Yaser", "Yasser", "Yasir"],
        "نواف": ["Nawaf"],
        "نايف": ["Naif", "Nayef"],
        "فهد": ["Fahad", "Fahd"],
        "إسماعيل": ["Ismail", "Esmail"],
        "مازن": ["Mazen", "Mazin"],
        "مراد": ["Murad", "Mourad"],
        "طارق": ["Tarek", "Tareq", "Tariq"],
        "وليد": ["Walid", "Waleed"],
        "كريم": ["Karim", "Kareem"],
        "سامي": ["Sami", "Samy"],
        "سامح": ["Sameh", "Samih"],
        "فارس": ["Faris", "Fares"],
        "حاتم": ["Hatem", "Hatim"],
        "حمد": ["Hamad", "Hammad"],
        "حامد": ["Hamed", "Hamid"],
        "هاني": ["Hani", "Hany"],
        "هشام": ["Hisham", "Hesham"],
        "هيثم": ["Haitham", "Haytham"],
        "بسام": ["Bassam", "Basam"],
        "بشار": ["Bashar"],
        "جمال": ["Jamal", "Gamal"],
        "كمال": ["Kamal", "Kemal"],
        "عماد": ["Emad", "Imad"],
        "عادل": ["Adel", "Adil"],
        "عبدالحميد": ["Abdulhamid", "Abdelhamid"],
        "عبدالناصر": ["Abdulnasser", "Abdelnasser"],
        "صلاح": ["Salah"],
        "شريف": ["Sharif", "Sherif"],
        "مصطفى": ["Mustafa", "Moustafa", "Mostafa"],
        "منصور": ["Mansour", "Mansoor"],
        "منذر": ["Munther"],
        "منير": ["Munir", "Muneer"],
        "منيب": ["Muneeb"],
        "فايز": ["Fayez"],
        "رامي": ["Rami", "Ramy"],
        "سيف": ["Saif", "Seif"],
        "راكان": ["Rakan"],
        "ركان": ["Rakan"],
        "ثامر": ["Thamer", "Thamir"],
        "أيمن": ["Ayman", "Aiman"],
        "فادي": ["Fadi"],
        "عدنان": ["Adnan"],
        "بلال": ["Bilal", "Belal"],
        "باسل": ["Basel", "Basil"],
        "باسم": ["Bassem", "Basim"],
        "قاسم": ["Qasim", "Qassim", "Kassem"],
        "حسام": ["Husam", "Hossam", "Hussam"],
        "مهند": ["Muhannad", "Mohanad", "Mohannad"],
        "سامر": ["Samer"],
        "سمير": ["Samir", "Sameer"],
        "مالك": ["Malek", "Malik"],
        "منتصر": ["Muntasir", "Montaser"],
        "معاذ": ["Muath", "Moath", "Muadh"],
        "هاشم": ["Hashem", "Hashim"],
        "نضال": ["Nidal"],
        "نزار": ["Nizar"],
        "وسام": ["Wisam", "Wesam"],
        "وائل": ["Wael", "Wail"],
        "وجدي": ["Wajdi", "Wagdy"],
        "إسحاق": ["Ishaq", "Isaac"],
        "إسلام": ["Islam", "Eslam"],
        "إلياس": ["Elias", "Ilyas"],
        "ادريس": ["Idris", "Edris"],
        "أكرم": ["Akram"],
        "أمير": ["Amir", "Ameer"],
        "أمين": ["Amin", "Ameen"],
        "أيوب": ["Ayoub", "Ayyub", "Ayob"],
        "بكر": ["Bakr", "Bakir"],
        "حافظ": ["Hafez", "Hafiz"],
        "رشيد": ["Rashid", "Rasheed"],
        "رضا": ["Reda", "Ridha"],
        "سلمان": ["Salman"],
        "سليمان": ["Suleiman", "Sulaiman"],
        "شعيب": ["Shoaib", "Shuaib"],
        "شهاب": ["Shihab", "Shehab"],
        "صديق": ["Siddiq", "Seddiq"],
        "عبدالقادر": ["Abdulqader", "Abdelkader"],
        "علاء": ["Alaa"],
        "فؤاد": ["Fuad", "Foad"],
        "كاظم": ["Kazem", "Kadhim"],
        "مهدي": ["Mahdi", "Mehdi"],
        "نبيل": ["Nabil", "Nabeel"],
        "يونس": ["Younis", "Younes", "Yunus"],
        "زكريا": ["Zakaria", "Zakariya"],
        "جابر": ["Jaber", "Jabir"],
        "جاسم": ["Jasim", "Jassim"],
        "مبارك": ["Mubarak", "Mobarak"],
        "متعب": ["Muteb"],
    },
    "F": {
        "فاطمة": ["Fatima", "Fatimah", "Fatma", "Fatemah"],
        "عائشة": ["Aisha", "Ayesha", "Aysha", "Ayisha"],
        "سارة": ["Sara", "Sarah", "Sarra"],
        "هناء": ["Hanaa", "Hana", "Hanaah"],
        "حنان": ["Hanan", "Hanaan"],
        "بسمة": ["Basma", "Basmh", "Basmah"],
        "نور": ["Noor", "Nour", "Nor"],
        "مها": ["Maha"],
        "منال": ["Manal"],
        "ريم": ["Reem", "Rym", "Rim"],
        "بيان": ["Bayan", "Bayyan"],
        "رانيا": ["Rania", "Raniah"],
        "روان": ["Rawan", "Rouan"],
        "رؤى": ["Roaa", "Ruaa", "Rowaa", "Roa", "Rua"],
        "جوري": ["Jory", "Joury", "Jouri", "Jori"],
        "نورة": ["Noura", "Noora", "Nourah", "Norah", "Nora"],
        "لينا": ["Lina", "Lena", "Leena"],
        "لجين": ["Lujain", "Lojain", "Lujein"],
        "لمى": ["Lama", "Lamaa"],
        "مريم": ["Maryam", "Mariam", "Meryem", "Mariem"],
        "خديجة": ["Khadija", "Khadijah", "Khadeja"],
        "أسماء": ["Asma", "Asmaa", "Asmah"],
        "إيمان": ["Eman", "Iman", "Imen"],
        "أماني": ["Amani", "Amany"],
        "أمل": ["Amal", "Amaal"],
        "هدى": ["Huda", "Houda", "Hoda"],
        "غادة": ["Ghadah", "Ghada"],
        "عبير": ["Abeer", "Abir"],
        "شوق": ["Shoug", "Shouq", "Shawq", "Shawg"],
        "أشواق": ["Ashwaq", "Ashwag"],
        "تهاني": ["Tahani", "Tahany"],
        "لطيفة": ["Latifa", "Lateefa", "Latifah"],
        "رحمة": ["Rahmah", "Rahma"],
        "سمية": ["Sumayyah", "Somaya", "Somia", "Sumayya"],
        "آلاء": ["Alaa", "Ala", "Aala"],
        "أريج": ["Areej", "Areeg", "Arij"],
        "بشاير": ["Bshair", "Bashayer", "Bashair"],
        "هاجر": ["Hajar", "Hagar"],
        "شهد": ["Shahd", "Shahad"],
        "ياسمين": ["Yasmin", "Jasmine", "Yasmine"],
        "ميار": ["Mayar"],
        "جنى": ["Jana", "Janah"],
        "ليان": ["Layan", "Lyan"],
        "دانة": ["Danah", "Dana"],
        "ملاك": ["Malak"],
        "سوزان": ["Susan", "Suzan", "Suzanne"],
        "إلهام": ["Elham", "Ilham"],
        "سامية": ["Samia", "Samiyah"],
        "بشرى": ["Bushra", "Boshra", "Boushra"],
        "حنين": ["Haneen", "Hanin"],
        "أحلام": ["Ahlam", "Ahlaam"],
        "إيلاف": ["Elaf", "Ilaaf"],
        "ربى": ["Ruba", "Rubaa", "Roba"],
        "مرام": ["Maram"],
        "مروة": ["Marwa", "Marwah"],
        "رواية": ["Rawiah", "Rawya"],
        "نوف": ["Noof", "Nouf"],
        "دعاء": ["Doaa", "Dua"],
        "دانية": ["Daniya", "Daniyah"],
        "ديانا": ["Diana", "Dyana"],
        "دنيا": ["Donia", "Donya", "Dunia"],
        "ميسون": ["Maysoon"],
        "منى": ["Mona", "Muna"],
        "نادية": ["Nadia", "Nadiyah"],
        "نهى": ["Nuha", "Noha"],
        "هند": ["Hind", "Hend"],
        "عزة": ["Azza"],
        "فردوس": ["Ferdous", "Firdaus"],
        "هانية": ["Haniya", "Hania"],
        "ليلى": ["Layla", "Laila", "Leila"],
        "لمياء": ["Lamia", "Lamya"],
        "لارا": ["Lara"],
        "منيرة": ["Munira", "Muneera"],
        "نجلاء": ["Najlaa", "Najla"],
        "نسرين": ["Nesrine", "Nasreen"],
        "هيا": ["Haya", "Hayah"],
        "وداد": ["Wedad", "Widad"],
        "وفاء": ["Wafaa", "Wafa"],
        "وردة": ["Warda", "Wardah"],
        "إيناس": ["Inas", "Enas"],
        "إسراء": ["Esraa", "Israa", "Isra"],
        "إنجي": ["Engy", "Ingy"],
        "تالا": ["Tala", "Talaa"],
        "تالين": ["Talin", "Taleen"],
        "ميرا": ["Mira", "Meera"],
        "ميساء": ["Maysaa", "Maysa"],
        "جواهر": ["Jawaher", "Jawahir"],
        "جوهرة": ["Joharah", "Jawhara", "Jouhara"],
        "سدرة": ["Sidra", "Sedra"],
        "شفاء": ["Shifa", "Shifaa"],
        "شيخة": ["Shaikha", "Sheikha"],
        "العنود": ["Anoud", "Alanoud"],
        "الجوهرة": ["Aljawhara", "Aljouhara"],
    },
}

CURATED = [
    ("Hanan", "F"), ("Basma", "F"), ("Sara", "F"), ("Sarah", "F"), ("Susan", "F"),
    ("Maha", "F"), ("Danah", "F"), ("Elham", "F"), ("Fatimah", "F"), ("Aisha", "F"),
    ("Samyah", "F"), ("Amani", "F"), ("Bushra", "F"), ("Manal", "F"), ("Reem", "F"),
    ("Haneen", "F"), ("Nourah", "F"), ("Ahlam", "F"), ("Elaf", "F"), ("Ruba", "F"),
    ("Bayan", "F"), ("Latifa", "F"), ("Rahmah", "F"), ("Abeer", "F"), ("Ghadah", "F"),
    ("Maram", "F"), ("Huda", "F"), ("Shahd", "F"), ("Rawiah", "F"), ("Noof", "F"),
    ("Hajar", "F"), ("Yasmin", "F"), ("Jasmine", "F"), ("Khadija", "F"),
    ("Lujain", "F"), ("Layan", "F"),
    ("Faisal", "M"), ("Mohammed", "M"), ("Mohammad", "M"), ("Mohamed", "M"),
    ("Muhammad", "M"), ("Ahmad", "M"), ("Ahmed", "M"), ("Ali", "M"),
    ("Omar", "M"), ("Omer", "M"), ("Abdullah", "M"), ("Abdallah", "M"),
    ("Abdulaziz", "M"), ("Abdulrahman", "M"), ("Khaled", "M"), ("Khalid", "M"),
    ("Saleh", "M"), ("Badr", "M"), ("Naif", "M"), ("Yahya", "M"),
    ("Mahmoud", "M"), ("Mahmud", "M"), ("Hassan", "M"), ("Hasan", "M"),
    ("Hussein", "M"), ("Hussain", "M"), ("Yaser", "M"), ("Yasser", "M"),
    ("Anas", "M"), ("Hamza", "M"), ("Ziyad", "M"), ("Zaid", "M"),
    ("Saad", "M"), ("Sultan", "M"), ("Turki", "M"), ("Fahad", "M"),
    ("Hamed", "M"), ("Hameed", "M"), ("Ibrahim", "M"), ("Ebrahim", "M"),
    ("Ismail", "M"), ("Saleem", "M"), ("Mohanad", "M"), ("Rakan", "M"),
    ("Thamer", "M"), ("Ayman", "M"), ("Murad", "M"), ("Mazen", "M"),
    ("Qasem", "M"), ("Adnan", "M"), ("Tarek", "M"), ("Tareq", "M"),
]


def normalize(s: str) -> str:
    s = s.lower().strip()
    s = re.sub(r"[^a-z\s]+", " ", s)
    s = re.sub(r"\s+", " ", s).strip()
    return s


def main():
    rows = []
    for sex, table in VARIANTS.items():
        for ar, lats in table.items():
            for lat in lats:
                rows.append((lat.strip(), sex))
    rows.extend(CURATED)

    by_norm = defaultdict(set)
    display_of = {}
    for lat, sex in rows:
        k = normalize(lat)
        if not k:
            continue
        by_norm[k].add(sex)
        if k not in display_of:
            display_of[k] = lat

    final = []
    skipped_ambiguous = 0
    for k, sexes in by_norm.items():
        if len(sexes) == 1:
            final.append((display_of[k], next(iter(sexes))))
        else:
            skipped_ambiguous += 1

    final.sort(key=lambda r: (r[1], r[0]))

    with open("Data/Seed/Latin_names.csv", "w", encoding="utf-8", newline="") as f:
        f.write("names,sex\n")
        for name, sex in final:
            f.write(f"{name},{sex}\n")

    m = sum(1 for _, s in final if s == "M")
    fcnt = sum(1 for _, s in final if s == "F")
    print(f"Wrote Data/Seed/Latin_names.csv: {len(final)} rows (M={m}, F={fcnt})")
    print(f"Skipped ambiguous Latin forms (both M+F): {skipped_ambiguous}")


if __name__ == "__main__":
    main()
