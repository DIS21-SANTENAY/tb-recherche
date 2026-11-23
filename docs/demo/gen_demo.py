# Génère un profil Thunderbird fictif (mbox) pour les captures du README de TB Recherche.
# Tous les noms, sociétés et adresses sont inventés (domaines en .example).
import io, os, sys, zipfile, random
from datetime import datetime, timedelta, timezone
from email.message import EmailMessage
from email.utils import format_datetime, make_msgid
from email import policy

if len(sys.argv) != 2:
    sys.exit("Usage : python gen_demo.py <dossier du profil à créer>")
OUT = sys.argv[1]
random.seed(21)
TZ = timezone(timedelta(hours=1))
NOW = datetime(2026, 11, 23, 18, 30, tzinfo=TZ)

ME = ("Claire Morel", "claire@domaine-des-tilleuls.example")
COMPTA = ("Domaine des Tilleuls - Comptabilité", "compta@domaine-des-tilleuls.example")

# ---------- pièces jointes ----------

def pdf(lines, title="Document"):
    """PDF minimal avec une vraie couche texte (Helvetica, WinAnsi)."""
    def esc(s):
        return s.replace("\\", "\\\\").replace("(", "\\(").replace(")", "\\)")
    ops = ["BT", "/F2 16 Tf", "56 780 Td", f"({esc(title)}) Tj", "/F1 10.5 Tf", "0 -30 Td", "14 TL"]
    for l in lines:
        ops.append(f"({esc(l)}) Tj T*")
    ops.append("ET")
    stream = "\n".join(ops).encode("cp1252")
    objs = [
        b"<< /Type /Catalog /Pages 2 0 R >>",
        b"<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        b"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Contents 4 0 R /Resources << /Font << /F1 5 0 R /F2 6 0 R >> >> >>",
        b"<< /Length " + str(len(stream)).encode() + b" >>\nstream\n" + stream + b"\nendstream",
        b"<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
        b"<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold /Encoding /WinAnsiEncoding >>",
    ]
    out = io.BytesIO()
    out.write(b"%PDF-1.4\n%\xe2\xe3\xcf\xd3\n")
    offs = []
    for i, o in enumerate(objs, 1):
        offs.append(out.tell())
        out.write(f"{i} 0 obj\n".encode() + o + b"\nendobj\n")
    x = out.tell()
    out.write(f"xref\n0 {len(objs)+1}\n0000000000 65535 f \n".encode())
    for o in offs:
        out.write(f"{o:010d} 00000 n \n".encode())
    out.write(f"trailer\n<< /Size {len(objs)+1} /Root 1 0 R >>\nstartxref\n{x}\n%%EOF\n".encode())
    return out.getvalue()

def xml_esc(s):
    return s.replace("&", "&amp;").replace("<", "&lt;").replace(">", "&gt;")

def docx(paras):
    body = "".join(f'<w:p><w:r><w:t xml:space="preserve">{xml_esc(p)}</w:t></w:r></w:p>' for p in paras)
    b = io.BytesIO()
    with zipfile.ZipFile(b, "w", zipfile.ZIP_DEFLATED) as z:
        z.writestr("[Content_Types].xml", '<?xml version="1.0" encoding="UTF-8"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override PartName="/word/document.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml"/></Types>')
        z.writestr("_rels/.rels", '<?xml version="1.0" encoding="UTF-8"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="word/document.xml"/></Relationships>')
        z.writestr("word/document.xml", f'<?xml version="1.0" encoding="UTF-8"?><w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"><w:body>{body}</w:body></w:document>')
    return b.getvalue()

def xlsx(rows):
    strings, idx = [], {}
    def si(s):
        if s not in idx:
            idx[s] = len(strings); strings.append(s)
        return idx[s]
    cols = "ABCDEFGH"
    xr = []
    for r, row in enumerate(rows, 1):
        cells = []
        for c, v in enumerate(row):
            ref = f"{cols[c]}{r}"
            if isinstance(v, (int, float)):
                cells.append(f'<c r="{ref}"><v>{v}</v></c>')
            else:
                cells.append(f'<c r="{ref}" t="s"><v>{si(v)}</v></c>')
        xr.append(f'<row r="{r}">{"".join(cells)}</row>')
    b = io.BytesIO()
    with zipfile.ZipFile(b, "w", zipfile.ZIP_DEFLATED) as z:
        z.writestr("[Content_Types].xml", '<?xml version="1.0" encoding="UTF-8"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/><Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/><Override PartName="/xl/sharedStrings.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sharedStrings+xml"/></Types>')
        z.writestr("_rels/.rels", '<?xml version="1.0" encoding="UTF-8"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/></Relationships>')
        z.writestr("xl/workbook.xml", '<?xml version="1.0" encoding="UTF-8"?><workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><sheets><sheet name="Feuil1" sheetId="1" r:id="rId1"/></sheets></workbook>')
        z.writestr("xl/_rels/workbook.xml.rels", '<?xml version="1.0" encoding="UTF-8"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/><Relationship Id="rId2" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/sharedStrings" Target="sharedStrings.xml"/></Relationships>')
        z.writestr("xl/worksheets/sheet1.xml", f'<?xml version="1.0" encoding="UTF-8"?><worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheetData>{"".join(xr)}</sheetData></worksheet>')
        z.writestr("xl/sharedStrings.xml", '<?xml version="1.0" encoding="UTF-8"?><sst xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">' + "".join(f"<si><t>{xml_esc(s)}</t></si>" for s in strings) + "</sst>")
    return b.getvalue()

MIME = {".pdf": ("application", "pdf"),
        ".docx": ("application", "vnd.openxmlformats-officedocument.wordprocessingml.document"),
        ".xlsx": ("application", "vnd.openxmlformats-officedocument.spreadsheetml.sheet"),
        ".jpg": ("image", "jpeg")}

# ---------- mise en forme des mails ----------

STYLE = "font-family: Segoe UI, Arial, sans-serif; font-size: 14px; color: #2b2b2b; line-height: 1.5;"

def html_body(paras, sig):
    ps = "".join(f"<p>{p}</p>" for p in paras)
    s = "<br>".join(sig)
    return (f'<html><body><div style="{STYLE}">{ps}'
            f'<p style="margin-top:22px; color:#6b6b6b; font-size:13px; border-top:1px solid #e3e3e3; padding-top:10px">{s}</p>'
            f"</div></body></html>")

def text_body(paras, sig):
    import re
    clean = [re.sub(r"<[^>]+>", "", p) for p in paras]
    return "\n\n".join(clean) + "\n\n-- \n" + "\n".join(sig) + "\n"

mailboxes = {}  # chemin relatif du dossier : liste de (date, octets du message)

def add(box, frm, to, subject, when, paras, sig, attach=(), cc=None, html=True, extra_html=None):
    m = EmailMessage(policy=policy.SMTP)
    m["From"] = f'"{frm[0]}" <{frm[1]}>'
    m["To"] = ", ".join(f'"{n}" <{a}>' for n, a in to)
    if cc:
        m["Cc"] = ", ".join(f'"{n}" <{a}>' for n, a in cc)
    m["Subject"] = subject
    m["Date"] = format_datetime(when)
    m["Message-ID"] = make_msgid(domain=frm[1].split("@")[1])
    m["MIME-Version"] = "1.0"
    m.set_content(text_body(paras, sig))
    if html:
        m.add_alternative(extra_html or html_body(paras, sig), subtype="html")
    for name, data in attach:
        mt, st = MIME[os.path.splitext(name)[1]]
        m.add_attachment(data, maintype=mt, subtype=st, filename=name)
    raw = m.as_bytes()
    raw = b"\r\n".join((b">" + l if l.startswith(b"From ") else l) for l in raw.split(b"\r\n"))
    mailboxes.setdefault(box, []).append((when, raw))

def ago(days, hour=9, minute=0):
    d = NOW - timedelta(days=days)
    return d.replace(hour=hour, minute=minute, second=random.randint(0, 59))

# ---------- correspondants ----------

TL = ("Julien Lefèvre", "j.lefevre@transports-lefevre.example")
TL_SIG = ["Julien Lefèvre", "Transports Lefèvre - affrètement et messagerie", "03 80 00 41 12"]
CV = ("Sophie Garnier", "sophie.garnier@cartonnages-du-val.example")
CV_SIG = ["Sophie Garnier", "Service commercial", "Cartonnages du Val"]
CEP = ("Antoine Rey", "antoine@cave-le-cep.example")
CEP_SIG = ["Antoine Rey", "Cave Le Cep, 14 rue des Remparts, Lyon"]
VER = ("Nadia Benali", "n.benali@verrerie-saint-roch.example")
VER_SIG = ["Nadia Benali", "Verrerie Saint-Roch", "Administration des ventes"]
IMP = ("Marc Duval", "bat@imprimerie-duval.example")
IMP_SIG = ["Marc Duval", "Imprimerie Duval - étiquettes adhésives"]
CAB = ("Hélène Roussel", "h.roussel@cabinet-roussel.example")
CAB_SIG = ["Hélène Roussel", "Expert-comptable", "Cabinet Roussel"]
SAL = ("Salon des Vignerons", "inscriptions@salon-vignerons.example")
BAN = ("Banque des Coteaux", "ne-pas-repondre@banque-coteaux.example")
PAUL = ("Paul Morel", "paul@domaine-des-tilleuls.example")
ME_SIG = ["Claire Morel", "Domaine des Tilleuls", "06 00 00 00 21"]

IN = "ImapMail/imap.domaine-des-tilleuls.example/INBOX"
FOUR = "ImapMail/imap.domaine-des-tilleuls.example/INBOX.sbd/Fournisseurs"
CLI = "ImapMail/imap.domaine-des-tilleuls.example/INBOX.sbd/Clients"
SENT = "ImapMail/imap.domaine-des-tilleuls.example/Sent"
C_IN = "ImapMail/compta.domaine-des-tilleuls.example/INBOX"
C_FACT = "ImapMail/compta.domaine-des-tilleuls.example/Factures"
LOC = "Mail/Local Folders/Archives 2025"

# ---------- les mails ----------

devis_pdf = pdf([
    "Transports Lefèvre - SIRET 812 345 678 00021",
    "Devis n° TL-2026-114 du 19 novembre 2026",
    "",
    "Client : Domaine des Tilleuls",
    "Enlèvement : chai du domaine, quai de chargement",
    "Livraison : Cave Le Cep, Lyon (69002)",
    "",
    "Désignation                                  Qté     Prix HT",
    "Palette 80 x 120, 600 bouteilles, filmée       12     68,00 EUR",
    "Hayon élévateur à la livraison                  1     35,00 EUR",
    "Assurance ad valorem                            1     22,00 EUR",
    "",
    "Total HT : 873,00 EUR    TVA 20 % : 174,60 EUR    Total TTC : 1 047,60 EUR",
    "Délai : enlèvement sous 48 h, livraison J+1. Devis valable 30 jours.",
], "Devis transport")
add(FOUR, TL, [ME], "Devis transport 12 palettes Lyon", ago(4, 10, 42),
    ["Bonjour Madame Morel,",
     "Suite à votre demande, vous trouverez ci-joint notre devis pour l'enlèvement de <b>12 palettes</b> au domaine et leur livraison chez Cave Le Cep à Lyon.",
     "Le hayon est compris pour le déchargement, la rue étant étroite. Nous pouvons passer dès mercredi si le devis vous convient.",
     "Bien cordialement,"], TL_SIG, [("Devis-TL-2026-114.pdf", devis_pdf)])

add(SENT, ME, [TL], "Re: Devis transport 12 palettes Lyon", ago(3, 8, 15),
    ["Bonjour Julien,",
     "C'est d'accord pour le devis TL-2026-114. Les palettes seront prêtes mercredi à partir de 8 h, filmées et étiquetées.",
     "Merci de prévenir Antoine Rey une heure avant la livraison.",
     "Bonne journée,"], ME_SIG)

bdc = docx([
    "Cave Le Cep - Bon de commande n° 2026-187",
    "Date : 17 novembre 2026",
    "Fournisseur : Domaine des Tilleuls",
    "Cuvée Les Tilleuls blanc 2024 - 1 800 bouteilles",
    "Cuvée Les Tilleuls rouge 2023 - 3 600 bouteilles",
    "Conditionnement : cartons de 6, livraison sur palettes",
    "Livraison souhaitée avant le 1er décembre pour les fêtes",
    "Paiement à 30 jours fin de mois",
])
add(CLI, CEP, [ME], "Bon de commande fêtes de fin d'année", ago(6, 17, 5),
    ["Bonjour Claire,",
     "Comme convenu au téléphone, voici notre bon de commande pour les fêtes. Les volumes sont un peu plus importants que l'an dernier, le rouge 2023 part très bien.",
     "Est-ce possible de livrer avant le 1er décembre ?",
     "Merci et à bientôt,"], CEP_SIG, [("Bon-de-commande-2026-187.docx", bdc)])

add(SENT, ME, [CEP], "Re: Bon de commande fêtes de fin d'année", ago(5, 9, 30),
    ["Bonjour Antoine,",
     "Merci pour la commande. Je vois avec le transporteur pour une livraison la semaine prochaine, je vous confirme le jour.",
     "Belle journée,"], ME_SIG, cc=[PAUL])

add(CLI, CEP, [ME], "Re: Livraison mercredi", ago(1, 14, 12),
    ["Parfait, nous serons là pour réceptionner. Pensez à mettre le bon de livraison sur la première palette.",
     "Merci Claire."], CEP_SIG)

facture_cv = pdf([
    "Cartonnages du Val - SIRET 498 765 432 00017 - TVA FR21498765432",
    "Facture n° F2026-0871 du 10 novembre 2026",
    "",
    "Cartons 6 bouteilles bordelaises, impression une couleur     2 400   0,92 EUR",
    "Croisillons carton                                         2 400   0,18 EUR",
    "Intercalaires de palette                                     120   0,45 EUR",
    "",
    "Total HT : 2 694,00 EUR    TVA 20 % : 538,80 EUR    Total TTC : 3 232,80 EUR",
    "Échéance : 10 décembre 2026 - virement",
], "Facture")
add(C_FACT, CV, [COMPTA], "Facture F2026-0871", ago(13, 11, 2),
    ["Bonjour,", "Veuillez trouver ci-joint notre facture correspondant à la livraison du 7 novembre.", "Cordialement,"],
    CV_SIG, [("Facture-F2026-0871.pdf", facture_cv)])

add(FOUR, CV, [ME], "Votre commande de cartons est expédiée", ago(17, 15, 40),
    ["Bonjour,",
     "Votre commande de 2 400 cartons 6 bouteilles a quitté notre entrepôt ce jour. Livraison prévue vendredi matin, sur 4 palettes.",
     "Cordialement,"], CV_SIG)

tarif = xlsx([
    ["Verrerie Saint-Roch - Tarif 2027", "", ""],
    ["Référence", "Désignation", "Prix au mille HT"],
    ["BG75-T", "Bourgogne tradition 75 cl, feuille morte", 412],
    ["BG75-L", "Bourgogne lourde 75 cl, extra-blanc", 538],
    ["BG150", "Magnum 150 cl", 1290],
    ["Conditionnement", "Palette de 1 512 bouteilles, houssée", ""],
    ["Hausse moyenne", "4,5 % par rapport au tarif 2026", ""],
])
add(FOUR, VER, [ME], "Tarif bouteilles 2027", ago(9, 9, 12),
    ["Madame,",
     "Vous trouverez ci-joint notre tarif 2027. Pour toute commande passée avant le 15 décembre, l'ancien tarif reste appliqué.",
     "Nous restons à votre disposition,"], VER_SIG, [("Tarif-2027-Verrerie-Saint-Roch.xlsx", tarif)])

add(SENT, ME, [VER], "Re: Tarif bouteilles 2027", ago(8, 18, 3),
    ["Bonjour Madame Benali,",
     "Merci pour le tarif. Je souhaite commander avant le 15 décembre : 6 palettes de BG75-T et 1 palette de magnums. Pouvez-vous me faire une confirmation de commande ?",
     "Cordialement,"], ME_SIG)

bat = pdf([
    "Imprimerie Duval - Bon à tirer n° 5521",
    "Étiquette Les Tilleuls rouge 2024 - format 90 x 120 mm",
    "Papier vergé ivoire, dorure à chaud, vernis sélectif",
    "Quantité : 8 000 étiquettes + 8 000 contre-étiquettes",
    "Mentions : 13,5 % vol. - 750 ml - contient des sulfites",
    "Merci de retourner ce BAT signé avant lancement en production.",
], "Bon à tirer")
add(FOUR, IMP, [ME], "BAT étiquettes rouge 2024", ago(21, 16, 20),
    ["Bonjour,",
     "Voici le BAT des étiquettes du rouge 2024 avec la dorure demandée. Attention, le degré a été mis à jour à 13,5 %.",
     "Merci de nous le retourner signé pour lancer l'impression.",
     "Cordialement,"], IMP_SIG, [("BAT-5521-Tilleuls-rouge-2024.pdf", bat)])

add(C_IN, CAB, [COMPTA], "Pièces pour la clôture", ago(2, 10, 5),
    ["Bonjour Claire,",
     "Pour préparer la clôture, pourriez-vous m'envoyer les relevés bancaires d'octobre et novembre, ainsi que l'inventaire des stocks en bouteilles et en vrac au 30 novembre ?",
     "Je vous propose un rendez-vous le 9 décembre pour faire le point.",
     "Bien à vous,"], CAB_SIG)

add(C_IN, BAN, [COMPTA], "Votre relevé de compte de novembre est disponible", ago(0, 7, 2),
    ["Bonjour,", "Votre relevé de compte professionnel est disponible dans votre espace en ligne.", "Ce message est envoyé automatiquement."],
    ["Banque des Coteaux"])

promo = (f'<html><body><div style="{STYLE} max-width:600px">'
         '<img src="https://salon-vignerons.example/banniere.jpg" width="600" height="180" alt="Salon des Vignerons">'
         '<h2 style="color:#8a1f3d">Salon des Vignerons, édition de printemps</h2>'
         '<p>Les inscriptions des exposants sont ouvertes jusqu\'au 15 janvier. Stand de 6 m², table, nappage et verrerie fournis.</p>'
         '<img src="https://salon-vignerons.example/pixel.gif" width="1" height="1">'
         '</div></body></html>')
add(IN, SAL, [ME], "Inscriptions exposants ouvertes", ago(11, 6, 30),
    ["Les inscriptions des exposants sont ouvertes jusqu'au 15 janvier."], ["Salon des Vignerons"], extra_html=promo)

add(IN, PAUL, [ME], "Vendanges : planning des équipes", ago(70, 20, 44),
    ["Claire,", "Voici le planning des équipes pour la semaine prochaine. On commence par les blancs lundi, le rouge à partir de jeudi si la météo tient.",
     "Il faudra prévoir les caisses et 2 palettes de bacs supplémentaires."], ["Paul"])

add(IN, PAUL, [ME], "Réception du matériel de mise", ago(15, 12, 8),
    ["Le technicien passe jeudi pour régler la tireuse avant la mise en bouteilles. Je serai au chai."], ["Paul"], html=False)

# Mails de fond pour donner du volume, sur plusieurs mois
fond = [
    (IN, ("Lucie Fontaine", "lucie.fontaine@mail.example"), "Réservation dégustation samedi", "Bonjour, nous serions quatre pour une dégustation samedi après-midi. Est-ce possible vers 15 h ?"),
    (IN, ("Office de tourisme", "contact@tourisme-coteaux.example"), "Mise à jour de votre fiche", "Merci de vérifier vos horaires d'ouverture pour la saison d'hiver."),
    (CLI, ("Restaurant La Treille", "chef@la-treille.example"), "Commande carte des vins", "Bonjour, nous voudrions 60 bouteilles du blanc 2024 pour la nouvelle carte."),
    (CLI, ("Hôtel du Parc", "achats@hotel-du-parc.example"), "Demande de tarifs professionnels", "Pourriez-vous nous transmettre vos tarifs CHR pour 2027 ?"),
    (FOUR, ("Bouchons Martin", "commercial@bouchons-martin.example"), "Confirmation commande bouchons", "Votre commande de 10 000 bouchons naturels 49 x 24 est confirmée."),
    (FOUR, ("Tonnellerie Gauthier", "contact@tonnellerie-gauthier.example"), "Livraison des fûts", "Les 8 fûts de 228 litres seront livrés la semaine prochaine."),
    (C_IN, ("Douanes - service viticulture", "viti@douane.example"), "Rappel déclaration récapitulative mensuelle", "Rappel : la DRM du mois est à déposer avant le 10."),
    (C_IN, CAB, "Acompte d'impôt", "Bonjour Claire, je vous rappelle l'échéance de l'acompte de décembre."),
    (C_FACT, ("Transports Lefèvre", "facturation@transports-lefevre.example"), "Facture transport octobre", "Veuillez trouver notre facture pour les enlèvements d'octobre."),
    (C_FACT, ("Énergies du Centre", "factures@energies-centre.example"), "Votre facture d'électricité", "Votre facture est disponible dans votre espace client."),
    (LOC, ("Cave Le Cep", "antoine@cave-le-cep.example"), "Commande fêtes 2025", "Bonjour Claire, voici notre commande pour les fêtes 2025."),
    (LOC, ("Imprimerie Duval", "bat@imprimerie-duval.example"), "BAT étiquettes blanc 2023", "Voici le BAT des étiquettes du blanc 2023."),
    (LOC, ("Transports Lefèvre", "j.lefevre@transports-lefevre.example"), "Devis transport 8 palettes Paris", "Voici notre devis pour 8 palettes à destination de Paris."),
]
for i in range(3):
    for box, frm, subj, txt in fond:
        days = random.randint(20, 160) if box != LOC else random.randint(330, 600)
        s = subj if i == 0 else ("Re: " + subj if i == 1 else subj + " (suite)")
        add(box, frm, [ME], s, ago(days, random.randint(7, 19), random.randint(0, 59)), ["Bonjour,", txt, "Cordialement,"], [frm[0]], html=(i != 2))

# ---------- écriture du profil ----------

if os.path.isdir(OUT) and os.listdir(OUT):
    sys.exit(f"Le dossier {OUT} n'est pas vide : choisissez un dossier neuf.")
for box, msgs in mailboxes.items():
    path = os.path.join(OUT, box)
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "wb") as f:
        for when, raw in sorted(msgs, key=lambda x: x[0]):
            f.write(b"From - " + when.strftime("%a %b %d %H:%M:%S %Y").encode() + b"\r\n")
            f.write(b"X-Mozilla-Status: 0001\r\nX-Mozilla-Status2: 00000000\r\n")
            f.write(raw.rstrip(b"\r\n") + b"\r\n\r\n")

prefs = [
    ("mail.accountmanager.accounts", "account1,account2,account3"),
    ("mail.account.account1.server", "server1"),
    ("mail.account.account2.server", "server2"),
    ("mail.account.account3.server", "server3"),
    ("mail.server.server1.type", "imap"),
    ("mail.server.server1.name", "Domaine des Tilleuls"),
    ("mail.server.server1.hostname", "imap.domaine-des-tilleuls.example"),
    ("mail.server.server1.directory-rel", "[ProfD]ImapMail/imap.domaine-des-tilleuls.example"),
    ("mail.server.server2.type", "imap"),
    ("mail.server.server2.name", "Comptabilité"),
    ("mail.server.server2.hostname", "compta.domaine-des-tilleuls.example"),
    ("mail.server.server2.directory-rel", "[ProfD]ImapMail/compta.domaine-des-tilleuls.example"),
    ("mail.server.server3.type", "none"),
    ("mail.server.server3.hostname", "Local Folders"),
    ("mail.server.server3.directory-rel", "[ProfD]Mail/Local Folders"),
]
with open(os.path.join(OUT, "prefs.js"), "w", encoding="utf-8") as f:
    f.write("// Profil de démonstration TB Recherche\n")
    for k, v in prefs:
        f.write(f'user_pref("{k}", "{v}");\n')
total = sum(len(v) for v in mailboxes.values())
print(f"{total} messages dans {len(mailboxes)} dossiers, profil créé dans {OUT}")
