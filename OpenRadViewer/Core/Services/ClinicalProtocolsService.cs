// @purpose: Clinical report protocols and templates for MRI and CT examinations with auto-filling of patient demographic and DICOM metadata.
// @depends: OpenRadViewer.Core.Models
// @exports: ClinicalProtocolsService, ClinicalProtocol

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using OpenRadViewer.Core.Models;

namespace OpenRadViewer.Core.Services;

public class ClinicalProtocol
{
    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty; // "IRM" or "CT"
    public string BodyRegion { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public string DefaultContrast { get; set; } = "Nu";

    public bool IsEmergency => Id.StartsWith("URG_", StringComparison.OrdinalIgnoreCase);

    public override string ToString() => $"[{Category}] {Title}";
}

public static class ClinicalProtocolsService
{
    private static readonly List<ClinicalProtocol> _protocols = new();

    static ClinicalProtocolsService()
    {
        InitializeProtocols();
    }

    public static IReadOnlyList<ClinicalProtocol> Protocols => _protocols;

    private static void InitializeProtocols()
    {
        // ==================== PROTOCOLE IRM ====================

        _protocols.Add(new ClinicalProtocol
        {
            Id = "IRM_CERVICAL",
            Title = "IRM Coloană Cervicală",
            Category = "IRM",
            BodyRegion = "Coloană Cervicală",
            DefaultContrast = "Nu",
            Content =
@"COLOANA CERVICALA:
Fara modificari patologice de curbura in plan sagital sau coronal.
Fara modificari ale aliniamentului vertebral cervical.
Cordon medular cu semnal si morfologie IRM in limite normale.
Jonctiunea bulbo-medulara fara modificari IRM; amigdalele cerebeloase in pozitie corecta, normala, nu sunt herniate. 
Canal rahidian cervical liber.
Corpi vertebrali cu inaltime pastrata si contur regulat al platourilor vertebrale. 
Hernii intraspongioase.
Osteofite marginale antero-laterale etajate, mai pronunțate în segmentul C5-C6 cu tendinta la formare de punti osoase.
Artroză unco-vertebrală.
Artroza atlanto-odontoida.
Modificari de semnal de tip degenerativ la nivelul discurilor intervertebrale cervicale, gradul II dupa clasificarea Pfirrmann.
Protruzii discale situate la nivel: "
        });

        _protocols.Add(new ClinicalProtocol
        {
            Id = "IRM_TORACAL",
            Title = "IRM Coloană Toracală",
            Category = "IRM",
            BodyRegion = "Coloană Toracală",
            DefaultContrast = "Nu",
            Content =
@"COLOANA TORACALA:
Fara modificari patologice de curbura in plan sagital sau coronal. 
Fara modificari ale aliniamentului vertebral dorsal.
Canal spinal cervical/toracal cu calibru normal.
Cordon medular toracal cu grosime normală, fără modificări de semnal.
Corpi vertebrali cu inaltime pastrata si contur regulat al platourilor vertebrale. 
Hernii intraspongioase.
Modificari de semnal de tip degenerativ lipomatos (Modic II).
Osteofite marginale antero-laterale etajate, cu tendinta la formare de punti osoase.
Modificari de semnal de tip degenerativ la nivelul discurilor intervertebrale toracale, gradul II dupa clasificarea Pfirrmann.
Protruzii discale situate la nivel: "
        });

        _protocols.Add(new ClinicalProtocol
        {
            Id = "IRM_LOMBAR",
            Title = "IRM Coloană Lombară",
            Category = "IRM",
            BodyRegion = "Coloană Lombară",
            DefaultContrast = "Nu",
            Content =
@"COLOANA LOMBARA:
Fara modificari patologice de curbura in plan sagital sau coronal. 
Linii spinale normal aliniate.
Con medular cu apexul situat la nivel L1, cu semnal normal IRM.
Corpi vertebrali lombari cu înălțime normală, contur regulat.
Hernii intraspongioase.
Modificari de semnal de tip degenerativ lipomatos (Modic II).
Osteofite marginale antero-laterale etajate, mai pronunțate în segmentul L4-L5 cu tendinta la formare de punti osoase.
Artroza interapofizara.
Modificari de semnal de tip degenerativ la nivelul discurilor intervertebrale lombare, gradul II dupa clasificarea Pfirrmann.
Protruzii discale situate la nivel: "
        });

        _protocols.Add(new ClinicalProtocol
        {
            Id = "IRM_CRANIO",
            Title = "IRM Cranio-Cerebral",
            Category = "IRM",
            BodyRegion = "Cranio-Cerebral",
            DefaultContrast = "Nu",
            Content =
@"CRANIO-CEREBRAL:
Arii in hipersemnal T2 si FLAIR, localizate in substanta alba periventricular si juxtacortical, cu dimensiuni de 1,5 mm, fara restrictie de difuzie, sugestiva pentru leziune demielinizanta, probabil de origine vascular-ischemica.
Relief cortical corespunzator varstei.
Hipocamp bilateral cu cateva chisturi remanente milimetrice (fara semnificatie patologica). Troficitate hipocampica pastrata bilateral (Scheltens 0 bilateral).
Spatii perivasculare Virchow-Robin vizibile bilateral si in special la nivel temporal stang.
Structurile liniei mediene in planul de simetrie al capului.
Trunchi cerebral, fosa posterioara normale IRM.
Cisternele de la baza libere.
Sistem ventricular simetric, nedilatat.
Orbite, continut orbitar, chiasma optica normale IRM.
Hipofiza normala IRM. Nu se deceleaza modificari ale planseului selar.
Ureche medie si interna normale IRM.
Sept nazal usor deviat spre dreapta, cu pintene septal drept de 2 mm.
Nervi optici, trigemeni si acustico-vestibulari cu aspect IRM normal bilateral. 
Contact neuro-vascular in unghiul ponto-cerebelos stang, intre artera cerebeloasa antero-inferioara si nervii VII si VIII.
Chist retentional de mucoasa cu dimensiuni de 3 mm la nivelul sinusului maxilar stang. In rest, sinusurile fetei, celule etmoidale si mastoidiene normal aerate.
Pe secventele ANGIO-RM nu se constata anomalii de calibru si permeabilitate la nivelul sistemelor arteriale carotidian intern, vertebrobazilar si venos cerebral."
        });

        _protocols.Add(new ClinicalProtocol
        {
            Id = "IRM_HIPOFIZA",
            Title = "IRM Hipofiză",
            Category = "IRM",
            BodyRegion = "Hipofiză / Șa Turcească",
            DefaultContrast = "Da",
            Content =
@"HIPOFIZA:
Loja hipofizara cu dimensiuni de 9/15/11 mm (ap/t/cc), contur net; glanda hipofiza cu inaltime maxima de 5 mm, cu contrastare omogena, fara a se decela leziuni nodulare hipointense postcontrast; tija hipofizara cu grosime normala (1,5 mm), usor lateralizata stanga.
Chiasma optica libera, la distanta de marginea superioara a hipofizei.
Sinusuri cavernoase libere, simetrice."
        });

        _protocols.Add(new ClinicalProtocol
        {
            Id = "IRM_GENUNCHI",
            Title = "IRM Genunchi",
            Category = "IRM",
            BodyRegion = "Genunchi",
            DefaultContrast = "Nu",
            Content =
@"GENUNCHI:
Revarsat fluid in / minima acumulare lichidiana in bursa supra, infra si retropatelara, in recesurile patelare medial si lateral, intraarticular si pericondilian, cu grosime maxima de 10 mm. Plica medio-patelara prezenta. 
Fina lama de lichid in bursa gastrocnemio-semimembranoasa cu grosime de 1,2 mm.
Ligamentele incrucisate anterior si posterior, colaterale lateral si medial, femuro-patelare cu insertii pastrate si semnal IRM normal.
Modificari de semnal de tip tendinopatic la nivelul tendonului cvadriceps si la insertia patelara a tendonului patelar.
Patela tip Wiberg II in pozitie normala in plan sagital (indice Insall-Salvati=1,15 VN 0.8-1.5) si axial.
Leziuni de cartilaj de grad 3-4 la nivelul condililor femurali, cu fisuri profunde ce ocupa intreaga grosime a cartilajului, mai avansate in regiunea medie si posterioara a acestora, la nivelul condilului femural extern asociind si mici osteofite subcondrale. Cartilajul ce acopera platourile tibiale cu semnal IRM in limite normale. Edem osos subcondral la nivelul condilului femural intern, ce asociaza cateva chisturi subcondrale, cu diametru de 3 mm. Leziuni de cartilaj de grad 3-4 in compartimentul femuro-patelar cu fisuri profunde ce ocupa intreaga grosime a cartilajului din dreptului apexului patelar si > 50% din grosime cartilajului trohlear.
Arie difuz conturata de edem osos, vizibila epifizar central la nivelul masivului spinos tibial medial.
Traiect in banda intrameniscal, cu hipersemnal PD Fat Sat si directie orizontala, fara contact cu suprafetele meniscale, vizibil in plan sagital si coronal la nivelul cornului posterior menisc intern – aspect sugestiv pentru leziune grad II (degenerare mixoida). 
Menisc extern cu semnal si morfologie normale IRM.
Doua chisturi artro-sinoviale cu diametre de pana la 13 mm posterior de condilul femural extern, respectiv de 8,6 mm in recesul sinovial central.
Ingustarea spatiului articular femuro-tibial, mai avansat in compartimentul intern; efilarea spinelor tibiale; minima osteofitoza marginala femuro-tibiala la toti versantii articulari si retropatelara, voluminoasa la acest nivel. 
Sunt vizibili doi corpi osteocondrali cu lama de fluid in jur in recesul ligamentului incrucisat posterior, cel mai mare de 4,7/5,7 mm si alti trei calcificati, posterior de platoul tibial extern, cel mai mare de 4,5/6 mm.
Doua enostoze la nivelul celor doi condili femurali, cu diametre de 7 mm la nivelul celui intern, respectiv de 3,5 mm pe cel extern.
Ganglion ovalar in spatiul popliteal cu dimensiuni de 11/7,5 mm.
Infiltrare edematoasa la nivelul grasimii Hoffa de-a lungul plicii infrapatelare si in partile moi de pe fata anterioara a genunchiului pre si infrapatelar.
Structurile vasculare din campul de examinare fara modificari IRM."
        });

        _protocols.Add(new ClinicalProtocol
        {
            Id = "IRM_MRCP",
            Title = "MRCP (Colangio-RM)",
            Category = "IRM",
            BodyRegion = "Căi Biliare & Pancreas (MRCP)",
            DefaultContrast = "Nu",
            Content =
@"MRCP:
Ficat cu diametrul antero-posterior al lobului drept ~ mm, al lobului stang ~ mm, fara leziuni focale.
Canal hepatic drept cu calibru maxim de mm.
Canal hepatic stang cu calibru maxim de mm.
Canal hepatic comun cu calibru maxim de mm.
Cai biliare intrahepatice nedilatate.
Colecist destins, cu continut lichidian; (contine mai multi calculi, cel mai mare cu diametrul de mm).
Calea biliara principala cu calibru de mm, fara calculi.
Pancreas - fara modificari de semnal. Canal Wirsung vizibil, nedilatat.
Rinichi, splina, glande suprarenale, pancreas cu aspect IRM in limite normale.
Nu se constata adenopatii abdominale. Cativa limfonoduli cu axul scurt infracentimetric in radacina mezenterului, lomboaortici.
Nu se constata lichid liber intraperitoneal."
        });

        _protocols.Add(new ClinicalProtocol
        {
            Id = "IRM_ABDOMEN",
            Title = "IRM Abdomen",
            Category = "IRM",
            BodyRegion = "Abdomen",
            DefaultContrast = "Da",
            Content =
@"ABDOMEN:
Ficat cu diametrul antero-posterior al lobului drept ~ mm, al lobului stang ~ mm, structura omogena, fara leziuni focale.
Colecist cu continut lichidian, fara calculi, perete normal.
Cai biliare intra si extrahepatice nedilatate. Vena porta cu calibru normal, permeabila.
Rinichi, splina, glande suprarenale, pancreas cu aspect IRM in limite normale.
Nu se constata adenopatii abdominale. 
Nu se constata lichid liber intraperitoneal."
        });

        _protocols.Add(new ClinicalProtocol
        {
            Id = "IRM_HEPATIC",
            Title = "IRM Hepatic - CH",
            Category = "IRM",
            BodyRegion = "Ficat & Căi Biliare",
            DefaultContrast = "Da",
            Content =
@"IRM HEPATIC- CH:
Ficat cu diametrul antero-posterior al lobului drept ~ mm, al lobului stang ~ mm, al lobului caudat ~ mm, dismorfic prin hipotrofia segmentului IV si hipertrofia segmentelor II-III, contur crenelat, structura neomogena.
Sistem venos port permeabil, nedilatat. 
Splina cu dimensiuni normale, omogena.
Colecist cu continut lichidian, fara calculi, perete normal. Cai biliare nedilatate.
Nu se constata adenopatii abdominale. 
Nu se constata lichid liber intraperitoneal."
        });

        _protocols.Add(new ClinicalProtocol
        {
            Id = "IRM_PELVIS",
            Title = "IRM Pelvis",
            Category = "IRM",
            BodyRegion = "Pelvis",
            DefaultContrast = "Da",
            Content =
@"PELVIS:
Vezica urinara in repletie/semirepletie/evacuata, perete normal.
Uter cu dimensiuni  mm (ap/t/cc), endometru cu grosime mm, miometru cu structura omogena. Ovar drept, ovar stang cu dimensiuni normale, structura normala. // Prostata, vezicule seminale fara modificari.
Nu se constata lichid liber pelvin sau adenopatii pelvine. 
Nu se constata leziuni osoase suspecte de malignitate in volumul examinat."
        });

        _protocols.Add(new ClinicalProtocol
        {
            Id = "IRM_ENTERO",
            Title = "Entero-RM",
            Category = "IRM",
            BodyRegion = "Tract Digestiv / Entero-RM",
            DefaultContrast = "Da",
            Content =
@"ENTERO-RM:
Stomac destins lichidian preprocedural, fara modificari parietale decelabile IRM.
Anse de intestin subtire destinse preprocedural; in aceste conditii nu se deceleaza modificari parietale sau de calibru.
Colon cu pregatire digestiva, cu continut lichidian si aeric, haustre pastrate, fara ingrosari parietale decelabile in conditiile de examinare. Nu se constata modificari in grasimea mezenterica si pericolica.
Nu se deceleaza lichid liber intraperitoneal sau adenopatii.
In rest, ficat, splina, rinichi, pancreas, glande suprarenale - fara modificari IRM."
        });

        _protocols.Add(new ClinicalProtocol
        {
            Id = "IRM_BAZIN",
            Title = "IRM Bazin (Artic. Coxo-Femurale)",
            Category = "IRM",
            BodyRegion = "Bazin & Articulații Coxo-Femurale",
            DefaultContrast = "Nu",
            Content =
@"BAZIN:
Spatii articulare coxo-femurale pastrate, interliniu articular cu calibru normal, simetric bilateral.
Fara revarsat fluid intraarticular coxo-femural.
Cap femural bilateral cu sfericitate pastrata, contur regulat, fara eroziuni marginale, fara modificari de semnal la nivelul osului subcondral.
Fara arii de edem osos pe topografia capului si colului femural, bilateral.
Versanti acetabulari cu contur regulat, fara eroziuni marginale, fara arii de edem osos spongios subcondral sau imagini chistice subcondrale.
Fara leziune de labrum.
Fara epansament lichidian in bursele ischiatica, gluteala, trohanteriana si ilio-psoasului.
Concluzii:
Aspectul IRM bazin (articulatii coxo-femurale) este in limite normale."
        });

        // ==================== PROTOCOLE CT ====================

        _protocols.Add(new ClinicalProtocol
        {
            Id = "CT_CRANIO",
            Title = "CT Craniocerebral",
            Category = "CT",
            BodyRegion = "Craniocerebral",
            DefaultContrast = "Nu",
            Content =
@"CRANIOCEREBRAL:
Atrofie cortico-subcorticala supra- si infratentoriala cu discreta largire a spatiilor lichidiene extranevraxiale si a sistemului ventricular, mai evident fronto-parietal, simetric, in limitele varstei.
Discreta leucoaraioza.
Arie hipodensa, cu densitati similar LCR localizata in substanta alba de la nivelul lobului frontal stang, cu dimensiuni de 12/9 mm (ap/t) - caracteristici CT sugestive pentru arie de AVC cronic.
Cisternele de la baza sunt libere. 
Trunchi cerebral cu aspect CT normal.
Fara elemente de semiologie CT sugestive pentru hemoragie subarahnoidiana, hematoame intra- sau extranevraxiale, accident vascular ischemic recent constituit in limita examinarii native.
Structurile liniei mediene situate in planul de simetrie al capului. 	
Calcificari in plexurile coroide, glanda pineala si in coasa creierului.
Ateromatoza calcificata in sistemul carotidian intern si vertebro-bazilar.
Ingrosare de mucoasa pana la 6 mm la nivelul sinusului sfenoidal. 
Minime ingrosari ale mucoasei sinusului maxilar stang, in dreptul recesurilor alveolare, cu grosime maxima de 5 mm.
Cateva celule mastoidiene obliterate bilateral.
Deviatia septului nazal spre dreapta. 
Hipertrofie cornete nazale inferioare. Concha bullosa bilateral."
        });

        _protocols.Add(new ClinicalProtocol
        {
            Id = "CT_TORACE",
            Title = "CT Torace",
            Category = "CT",
            BodyRegion = "Torace",
            DefaultContrast = "Da",
            Content =
@"TORACE:
Sindrom de condensare: absent.
Arbore traheo-bronsic permeabil.
Absenta adenopatiilor mediastinale.
Absenta revarsatelor lichidiene pleuro-pericardice.
Vase mediastinale permeabile, cu calibru normal.
Parenchim pulmonar bilateral fara leziuni evolutive decelabile CT."
        });

        _protocols.Add(new ClinicalProtocol
        {
            Id = "CT_ABDOMINOPELVIN",
            Title = "CT Abdomino-Pelvin",
            Category = "CT",
            BodyRegion = "Abdomino-Pelvin",
            DefaultContrast = "Da",
            Content =
@"ABDOMINO-PELVIN:
Ficat cu dimensiuni in limite normale / crescute (diametru antero-posterior al lobului drept de  mm, al lobului stang de  mm), structura omogena. 
Cai biliare intra- si extrahepatice nedilatate. 
Colecist destins, cu continut lichidian, perete cu grosime normala, fara calculi radioopaci. 
Sistem port cu calibru normal, permeabil (vena porta cu diametru de mm, mezenterica superioara mm, splenica de mm).
Pancreas, glande suprarenale cu aspect CT in limite normale. 
Splina cu dimensiuni de ~ mm (diametru maxim axial x grosime x lungime cranio-caudala), cu structura omogena.
Rinichi cu pozitie, dimensiuni si morfologie normale, cu secretie si excretie simetrice, in limite normale de timp. 
Nu se constata adenopatii abdomino-pelvine.
Nu se identifica lichid intraperitoneal.
Colon cu pregatire digestiva, a fost destins prin administrare de contrast neutru (apa) prin clisma. In conditiile de examinare nu se constata modificari parietale, de calibru sau formatiuni expansive intraluminale.
Stomac, anse intestinale destinse prin administrare de contrast neutru (apa), fara prize de contrast parietale patologice.
Vezica urinara, vezicule seminale cu aspect CT in limite normale.
Prostata cu dimensiuni de ~ (diametru antero-posterior x transversal x cranio-caudal), cu aspect CT normal.
Uter cu dimensiuni de ~ mm (diametru longitudinal x diametre transversale), cu aspect normal. Endometru cu grosimea de mm. Ovar drept si stang cu aspect CT normal.
Modificari degenerativ-artrozice la nivelul coloanei vertebrale cuprinse in volumul scanat, manifestate prin osteofite antero-marginale, cu formare de punti osteofitice, fenomene de vid discal, pensarea spatiilor.
Baze pulmonare cu aspect in limite normale."
        });

        _protocols.Add(new ClinicalProtocol
        {
            Id = "CT_TEP",
            Title = "CT TEP (Angio-CT Tromboembolism)",
            Category = "CT",
            BodyRegion = "Angio-CT Torace (TEP)",
            DefaultContrast = "Da",
            Content =
@"TEP (TROMBOEMBOLISM PULMONAR):
Trunchiul arterei pulmonare cu calibru de .. mm, artera pulmonara dreapta cu calibru de ... mm si stanga cu calibru de ... mm, fara defecte de umplere sugestive pentru trombembolism.
Nu se constata defecte de umplere la nivelul ramurilor lobare, segmentare si subsegmentare pulmonar bilateral.
Nu se constata arii de consolidare periferica cu aspect de infarct constituit pulmonar bilateral.
Absenta revarsatelor lichidiene pleuro-pericardice.
Arbore traheo-bronsic permeabil."
        });

        _protocols.Add(new ClinicalProtocol
        {
            Id = "CT_COLOANA_CERVICALA",
            Title = "CT Coloană Cervicală",
            Category = "CT",
            BodyRegion = "Coloană Cervicală",
            DefaultContrast = "Nu",
            Content =
@"COLOANA CERVICALA:
Buna aliniere a corpilor vertebrali cervicali in plan sagital, lordoza fiziologica pastrata / discreta tendinta la rectitudine.
Fara anomalii ale jonctiunii cranio-vertebrale sau atlanto-axiale. Artroza atlanto-odontoida.
Corpi vertebrali cervicali cu inaltime pastrata, contur regulat, fara tasari recente sau traiecte de fractura decelabile CT.
Structura osoasa trabeculara cu densitate in limite normale.
Osteofite marginale antero-laterale si posterioare etajate, mai evidente la nivelul segmentului C4-C7.
Modificari degenerative de tip uncartroza si artroza interapofizara etajata.
Pensari ale spatiilor discale intervertebrale la nivel: 
Mici hernii intraspongioase decelabile la nivelul platourilor vertebrale.
Protruzii / deborduri discale: 
Fara stenoze semnificative de canal rahidian osos cervical.
Gauri de conjugare libere bilateral.
Partile moi paravertebrale cervicale cu aspect CT in limite normale.

CONCLUZII:
Modificari spondilodiscartrozice cervicale etajate, fara leziuni post-traumatice acute."
        });

        _protocols.Add(new ClinicalProtocol
        {
            Id = "CT_COLOANA_TORACALA",
            Title = "CT Coloană Toracală",
            Category = "CT",
            BodyRegion = "Coloană Toracală",
            DefaultContrast = "Nu",
            Content =
@"COLOANA TORACALA:
Cifoza toracala fiziologica in limite normale / discret accentuata, fara anomalii de aliniament sau devieri in plan coronal.
Corpi vertebrali toracali cu inaltime pastrata, fara leziuni traumatice acute, tasari sau fracturi decelabile CT.
Osteofite marginale antero-laterale etajate, cu tendinta la formare de punti osoase in segmentul toracal mijlociu si inferior.
Artroza costo-vertebrala si interapofizara etajata.
Hernii intraspongioase la nivelul platourilor vertebrale toracale.
Pensari ale spatiilor discale intervertebrale la nivel: 
Deborduri discale: 
Canal spinal osos toracal cu diametre in limite normale.
Gauri de conjugare cu calibru pastrat bilateral.
Parti moi paravertebrale toracale fara modificari patologice decelabile CT.

CONCLUZII:
Modificari spondilodiscartrozice toracale etajate, fara leziuni post-traumatice acute decelabile CT."
        });

        _protocols.Add(new ClinicalProtocol
        {
            Id = "CT_COLOANA_LOMBARA",
            Title = "CT Coloană Lombară",
            Category = "CT",
            BodyRegion = "Coloană Lombară",
            DefaultContrast = "Nu",
            Content =
@"COLOANA LOMBARA:
Lordoza lombara fiziologica pastrata / tendinta la rectitudine a coloanei vertebrale lombare.
Aliniament vertebral posterior pastrat, fara spondilolisteza decelabila CT.
Corpi vertebrali lombari cu inaltime pastrata, contur regulat al platourilor vertebrale, fara tasari sau fracturi osoase acute.
Structura osoasa cu mineralizare in limite normale / demineralizare difuza de tip osteopenic.
Osteofite marginale antero-laterale etajate, mai evidente la nivelul segmentelor L4-L5 si L5-S1, cu tendinta la formare de punti osoase; mici osteofite marginale posterioare.
Hernii intraspongioase la nivelul platourilor vertebrale.
Modificari degenerative de tip artroza interapofizara, mai accentuata lombar inferior.
Pensari ale spatiilor discale intervertebrale, mai exprimate la nivel L4-L5 si L5-S1, asociind fenomen de vid discal si scleroza subcondrala a platourilor adiacente.
Deborduri discale / protruzii la nivel: 
Canal rahidian osos lombar cu calibru normal, fara stenoze osoase constitutionale sau dobandite.
Gauri de conjugare libere bilateral.
Partile moi paravertebrale lombare cu aspect CT in limite normale.

CONCLUZII:
Modificari spondilodiscartrozice lombare etajate, mai evidente L4-S1, fara leziuni traumatice acute decelabile CT."
        });

        _protocols.Add(new ClinicalProtocol
        {
            Id = "CT_GENUNCHI",
            Title = "CT Genunchi",
            Category = "CT",
            BodyRegion = "Genunchi",
            DefaultContrast = "Nu",
            Content =
@"CT GENUNCHI:
Raporturi articulare femuro-tibiale si femuro-patelare pastrate, fara deplasari sau luxatii osoase.
Contururi si suprafete articulare regulate, fara solutii de continuitate corticala sau traiecte de fractura decelabile CT.
Spatii articulare femuro-tibiale simetrice / discreta pensare in compartimentul femuro-tibial intern.
Spine tibiale cu morfologie pastrata / discret efilate, cu minima scleroza subcondrala adiacenta.
Minime osteofite marginale femuro-tibiale si retropatelare.
Patela centrata in trohleea femurala, in pozitie normala in plan sagital si axial, fara semne de subluxatie.
Structura osoasa cu mineralizare pastrata / discreta demineralizare de tip osteopenic.
Fara imagini de geode subcondrale sau corpi liberi intraarticulari radioopaci.
Tendoanele cvadricipital si patelar cu traiect si grosime in limite normale decelabile CT nativ.
Pachete vasculare poplitee cu calibru normal; absenta calcificarilor parietale patologice.
Parti moi periarticulare fara anomalii densitometrice decelabile.

CONCLUZII:
Fara leziuni osoase traumatice acute decelabile CT la nivelul articulatiei genunchiului."
        });

        _protocols.Add(new ClinicalProtocol
        {
            Id = "CT_GLEZNA",
            Title = "CT Gleznă",
            Category = "CT",
            BodyRegion = "Gleznă & Picior",
            DefaultContrast = "Nu",
            Content =
@"CT GLEZNA:
Raporturi osoase normale in articulatia tibio-tarsiana (talocrurala), tibio-fibulare distale si subtalare.
Morfologie si densitate osoasa in limite normale la nivelul extremitatilor distale tibiala si peroniera, talusului, calcaneului si oaselor tarsiene vizibile.
Corticale osoase cu grosime si contur pastrate, fara traiecte de fractura recente, infundari ale platoului tibial sau tasari trabeculare decelabile CT.
Spatiu articular tibio-talar cu latime pastrata, fara pensare sau calcificari intraarticulare.
Fara osteofite marginale sau scleroza subcondrala la nivelul maleolelor tibiala si peroniera.
Sindesmoza tibio-peroniera distala cu spatiu conservat, fara diastazis osos.
Sustentaculum tali si procesul anterior al calcaneului cu aspect CT normal.
Tendonul achilean si tendoanele peroniere cu grosime si traiect in limite normale CT nativ.
Grasimea periarticulara si partile moi adiacente fara colectii fluide sau tumefactii evidente.

CONCLUZII:
Examen CT glezna fara modificari traumatice osoase acute (fara traiecte de fractura sau luxatii)."
        });

        _protocols.Add(new ClinicalProtocol
        {
            Id = "CT_COT",
            Title = "CT Cot",
            Category = "CT",
            BodyRegion = "Cot",
            DefaultContrast = "Nu",
            Content =
@"CT COT:
Raporturi articulare normale in articulatiile humero-ulnara, humero-radiala si radio-ulnara proximala, fara deplasari sau luxatii.
Contururi osoase nete, regulate la nivelul epifizei distale humerale (capitulum, trohlee), olecranului, procesului coronoid si capului/cupusoarei radiale.
Fara solutii de continuitate osoasa, tasari corticale sau traiecte de fractura decelabile CT nativ.
Spatii articulare humero-radiale si humero-ulnare cu dimensiuni pastrate, fara ingustari sau modificari osteofitice marginale.
Fara corpi liberi intraarticulari calcificati.
Fosa olecraniana, coronoida si radiala libere.
Linie radiocapitulara si linie humerala anterioara in limite normale.
Tendoanele bicepsului distal si tricepsului cu raporturi anatomice pastrate.
Tesuturi moi periarticulare cu densitate normala, fara revarsat sau tumefactie evidenta.

CONCLUZII:
Articulatie a cotului cu aspect CT in limite normale, fara leziuni osoase traumatice recente decelabile."
        });

        _protocols.Add(new ClinicalProtocol
        {
            Id = "CT_GAT",
            Title = "CT Gât (Structuri Cervicale + Contrast)",
            Category = "CT",
            BodyRegion = "Gât / Structuri Cervicale",
            DefaultContrast = "Da",
            Content =
@"CT GAT (STRUCTURILE CERVICALE):
Examinarea a fost efectuata cu administrare de substanta de contrast non-ionica intravenos, de la nivelul bazei craniului pana la nivelul aperturii toracice superioare.
Rinofaringe, orofaringe si hipofaringe cu morfologie si simetrie pastrate, fara ingrosari mucoase patologice sau mase expansive intraluminale. Spatiul parafaringian si retrofaringian cu densitati adipoase omogene, fara colectii sau mase infiltrative.
Laringe (epiglota, corzi vocale, spatiu paraglotic si subglotic) cu aspect CT normal, fara deformari sau prize de contrast asimetrice. Cartilaje laringiene cu calcificari normale pentru varsta, fara osteolize.
Glande parotide si submandibulare simetrice, cu dimensiuni normale, contur net si densitate/contrastare parenchimatoasa omogena, fara procese proliferative focale sau calculi ductali radioopaci.
Glanda tiroida cu pozitie normala, lobi tiroidieni simetrici, cu densitate si iodocaptare omogena, fara leziuni nodulare suspecte decelabile CT.
Ax vascular cervical (artere carotide comune, carotide interne si externe, vene jugulare interne) permeabil bilateral, cu opacifiere optima, calibru pastrat si traiect normal, fara stenoze semnificative sau tromboze vasculare.
Fara adenopatii cervicale decelabile CT; limfonoduli cu ax scurt infracentimetric vizibili in statiile ganglionare cervicale bilateral (jugulo-carotidiene si spinale), cu morfologie ovalara si centru adipos pastrat.
Structuri osoase cuprinse in volumul scanat (baza craniului, mandibula, coloana cervicala) fara leziuni osteolitice sau osteoblastice suspecte.
Fara colectii fluide sau formatiuni chistice la nivelul partilor moi cervicale.

CONCLUZII:
Aspect CT in limite normale la nivelul structurilor gatului si viscerocraniului inferior, fara mase expansive, procese inflamatorii sau adenopatii patologice."
        });

        // ==================== ARTICULAR CT PROTOCOLS ====================
        _protocols.Add(new ClinicalProtocol
        {
            Id = "CT_UMAR",
            Title = "CT Umăr",
            Category = "CT",
            BodyRegion = "Umăr / Articulație Scapulo-Humerală",
            DefaultContrast = "Nu",
            Content =
@"CT UMAR:
Examinare CT a articulatiei scapulo-humerale efectuata nativ cu reconstructii multiplanare in plan axial, coronal si sagital, cu fereastra de os si de parti moi.
Raporturi articulare scapulo-humerale si acromio-claviculare pastrate, congruenta articulara normala.
Cap humeral cu morfologie si densitate osoasa in limite normale, contur cortical net, fara solutii de continuitate corticala sau modificari de densitate trabeculara sugestive pentru fracturi.
Cavitate glenoida cu profunzime si contur osos pastrat, fara leziuni tip Bankart osos decelabile.
Spatiul subacromial de calibru normal. Acromion de tip I/II.
Fara calcificari grosiere periarticulare sau in proiectia insertiei tendoanelor coafei rotatorilor decelabile CT.
Fara epanșament lichidian intraarticular sau in bursa subacromio-subdeltoidiana.
Parti moi periarticulare cu aspect CT in limite normale.

CONCLUZII:
Articulatie scapulo-humerala cu aspect CT in limite normale, fara leziuni osoase traumatice recente decelabile."
        });

        _protocols.Add(new ClinicalProtocol
        {
            Id = "CT_PUMN",
            Title = "CT Pumn",
            Category = "CT",
            BodyRegion = "Pumn / Oase Carpiene",
            DefaultContrast = "Nu",
            Content =
@"CT PUMN:
Examinare CT a articulatiei radio-carpiene si a oaselor carpului efectuata nativ, cu reconstructii multiplanare de inalta rezolutie in ferestre osoase si de parti moi.
Raporturi articulare radio-carpiene, radio-ulnare distale si medio-carpiene pastrate.
Oase carpiene (scafoid, semilunar, piramidal, pisiform, trapez, trapezoid, capitat, hamat) cu morfologie si densitate in limite normale, fara traiecte de fractura recente decelabile. In particular, osul scafoid nu prezinta solutii de continuitate osoasa sau anomalii de densitate corticala/trabeculara.
Epifize distale radius si ulna cu contururi corticale nete, fara solutii de continuitate.
Fara calcificari patologice in partile moi carpiene sau formatiuni expansive.

CONCLUZII:
Articulatie radio-carpiana si oase carpiene cu aspect CT in limite normale, fara leziuni osoase traumatice recente decelabile (fara fractura de scafoid)."
        });

        _protocols.Add(new ClinicalProtocol
        {
            Id = "CT_BAZIN",
            Title = "CT Bazin",
            Category = "CT",
            BodyRegion = "Bazin / Articulații Coxo-Femurale",
            DefaultContrast = "Nu",
            Content =
@"CT BAZIN:
Examinare CT a centurii pelvine si a articulatiilor coxo-femurale efectuata nativ cu reconstructii multiplanare.
Continuitatea inelului pelvin este pastrata, fara leziuni osoase de tip fractura la nivelul oaselor iliace, ischionului, pubisului sau sacrului. Simfiza pubiana si articulatiile sacro-iliace cu raporturi anatomice normale, simetrice, fara disjunctii.
Articulatii coxo-femurale cu congruenta pastrata bilateral. Capete si coluri femurale cu forma si structura osoasa normale, contur sferic pastrat, fara fracturi sau zone de colaps cortical/osteonecroza.
Acetabule cu profunzime si sprancene acetabulare intacte, spatii articulare coxo-femurale de calibru pastrat simetric.
Fara acumulari fluide anormale intraarticulare sau colectii in partile moi periarticulare pelvine.

CONCLUZII:
Centura pelvina si articulatii coxo-femurale cu aspect CT in limite normale, fara traiecte de fractura sau disjunctii articulare decelabile."
        });

        _protocols.Add(new ClinicalProtocol
        {
            Id = "CT_COAPSA",
            Title = "CT Coapsă",
            Category = "CT",
            BodyRegion = "Coapsă / Femur",
            DefaultContrast = "Nu",
            Content =
@"CT COAPSA:
Examinare CT a segmentului femural si partilor moi adiacente efectuata nativ, cu reconstructii multiplanare.
Diafiza si metafizele femurale cu grosime si contur cortical normal, canal medular omogen, fara solutii de continuitate corticala, reactii periostale sau leziuni osteolitice/osteoblastice.
Mase musculare ale compartimentelor coapsei (anterior, posterior, medial) cu densitate, morfologie si volum normale, simetrice, fara colectii fluide, hematoame intramusculare sau mase expansive.
Fascia lata si tesutul celulo-adipos subcutanat cu densitate omogena.

CONCLUZII:
Aspect CT in limite normale la nivelul coapsei si osului femur, fara traiecte de fractura sau leziuni patologice in partile moi."
        });

        _protocols.Add(new ClinicalProtocol
        {
            Id = "CT_GAMBA",
            Title = "CT Gambă",
            Category = "CT",
            BodyRegion = "Gambă / Tibie-Peroneu",
            DefaultContrast = "Nu",
            Content =
@"CT GAMBA:
Examinare CT a gambei efectuata nativ, cu reconstructii multiplanare in fereastra osoasa si de parti moi.
Oase gambiere (tibie si fibula/peroneu) pe toata lungimea lor cu structura si densitate osoasa normale, contur cortical continuu si regulat, fara traiecte de fractura recente, deplasari osoase sau reactii periostale.
Membrana interosoasa si spatiul tibio-fibular cu raporturi normale.
Loje musculare gambiere cu densitate si simetrie pastrate, fara hematoame sau formatiuni expansive. Tesut adipos subcutanat omogen.

CONCLUZII:
Segment de gamba cu aspect CT in limite normale, fara leziuni de tip fractura tibio-peroniera sau anomalii de parti moi."
        });

        _protocols.Add(new ClinicalProtocol
        {
            Id = "CT_ANTEPICIOR",
            Title = "CT Antepicior",
            Category = "CT",
            BodyRegion = "Antepicior / Oase Metatarsiene",
            DefaultContrast = "Nu",
            Content =
@"CT ANTEPICIOR:
Examinare CT a regiunii metatarsiene si falangiene efectuata nativ cu reconstructii de inalta rezolutie.
Raporturi articulare tarso-metatarsiene (articulatia Lisfranc) si metatarso-falangiene normale, fara diastazis sau subluxatii.
Oase metatarsiene I-V si falange cu morfologie, corticala si structura trabeculara normale, fara solutii de continuitate osoasa sau fracturi de stress/traumatice decelabile.
Spatii articulare intermetatarsiene si interfalangiene pastrate.
Fara corpi straini radioopaci, colectii sau tumefieri focale patologice in partile moi plantare si dorsale ale antepiciorului.

CONCLUZII:
Examinare CT antepicior in limite normale, fara traiecte de fractura sau leziuni de tip luxatie decelabile CT."
        });

        // ==================== PROTOCOALE NORMALE DE URGENTA (URG CT) ====================
        // Plasate la finalul listei, ordonate, incepand toate cu prefixul 'URG CT '
        _protocols.Add(new ClinicalProtocol
        {
            Id = "URG_CT_CRANIO",
            Title = "URG CT CRANIO-CEREBRAL",
            Category = "CT",
            BodyRegion = "Cranio-Cerebral",
            DefaultContrast = "Nu",
            Content =
@"CRANIO-CEREBRAL:
	Cisternele de la baza sunt libere.
	Trunchi cerebral, fosa posterioara, sistem ventricular, emisfere cerebrale cu aspect CT normal.
	Fara elemente de semiologie CT sugestive pentru hemoragie subarahnoidiana, hematoame intra- sau extranevraxiale sau accident vascular ischemic constituit.
	Structurile liniei mediene situate in planul de simetrie al capului.
	Calcificari in plexurile coroide si in glanda pineala.
	Sinusuri paranazale și celule mastoidiene normal aerate."
        });

        _protocols.Add(new ClinicalProtocol
        {
            Id = "URG_CT_TORACE",
            Title = "URG CT TORACE",
            Category = "CT",
            BodyRegion = "Torace",
            DefaultContrast = "Nu",
            Content =
@"TORACE:
	Parenchim pulmonar cu aspect CT în limite normale.
	Arbore traheo-bronșic permeabil.
	Absenta adenopatiilor mediastinale.
	Absența revărsatelor lichidiene pleuro-pericardice.
	Vase mediastinale permeabile, cu calibru normal."
        });

        _protocols.Add(new ClinicalProtocol
        {
            Id = "URG_CT_TEP",
            Title = "URG CT TEP",
            Category = "CT",
            BodyRegion = "Torace / TEP",
            DefaultContrast = "Da",
            Content =
@"TEP:
	Trunchiul arterei pulmonare cu calibru de .. mm, arteră pulmonară dreaptă cu calibru de ... mm și stângă cu calibru de ... mm, fără defecte de umplere sugestive pentru trombembolism.
	Nu se constată defecte de umplere la nivelul ramurilor lobare, segmentare și subsegmentare pulmonar bilateral.
	Nu se constată arii de consolidare periferică cu aspect de infarct constituit pulmonar bilateral.
	Absența revărsatelor lichidiene pleuro-pericardice.
	Arbore traheo-bronsic permeabil."
        });

        _protocols.Add(new ClinicalProtocol
        {
            Id = "URG_CT_ABDOMINOPELVIN",
            Title = "URG CT ABDOMINO-PELVIN",
            Category = "CT",
            BodyRegion = "Abdomino-Pelvin",
            DefaultContrast = "Da",
            Content =
@"ABDOMINO-PELVIN:
	Ficat cu dimensiuni in limite normale / crescute (diametru antero-posterior al lobului drept de  mm, al lobului stâng de  mm), structura omogena.
	Căi biliare intra- si extrahepatice nedilatate.
	Colecist destins, cu conținut lichidian, perete cu grosime normală, fără calculi radioopaci.
	Sistem port cu calibru normal, permeabil (venă portă cu diametru de mm, mezenterică superioară mm, splenica de mm).
	Pancreas, glande suprarenale cu aspect CT în limite normale.
	Splină cu dimensiuni de ~  mm (diametru maxim axial x grosime x lungime cranio-caudală), cu structură omogenă.
	Rinichi cu poziție, dimensiuni și morfologie normale, cu secreție și excreție simetrice, în limite normale de timp.
	Nu se constată adenopatii abdomino-pelvine.
	Nu se identifică lichid intraperitoneal.
	Colon cu pregătire digestivă, a fost destins prin administrare de contrast neutru (apă) prin clismă. În condițiile de examinare nu se constată modificări parietale, de calibru sau formațiuni expansive intraluminale.
	Stomac, anse intestinale destinse prin administrare de contrast neutru (apa), fara prize de contrast parietale patologice.
	Vezică urinară, vezicule seminale cu aspect CT în limite normale.
	Prostată cu dimensiuni de ~ (diametru antero-posterior x transversal x cranio-caudal), cu aspect CT normal.
	Vezica urinara, cu aspect CT normal.
	Uter cu dimensiuni de ~  mm (diametru longitudinal x diametre transversale), cu aspect normal. Endometru cu grosimea de  mm. Ovar drept cu dimensiuni de ~  (diametru antero-posterior x transversal x cranio-caudal) și stâng cu dimensiuni de  ~  (diametru antero-posterior x transversal x cranio-caudal), cu aspect CT normal.
	Modificari degenerativ-artrozice la nivelul coloanei vertebrale cuprinse in volumul scanat, manifestate prin osteofite antero-marginale, cu formare de punti osteofitice, fenomene de vid discal, pensarea spatiilor.
	Baze pulmonare cu aspect în limite normale."
        });

        _protocols.Add(new ClinicalProtocol
        {
            Id = "URG_CT_TAP",
            Title = "URG CT TAP",
            Category = "CT",
            BodyRegion = "Toraco-Abdomino-Pelvin",
            DefaultContrast = "Da",
            Content =
@"TORACE:
	Parenchim pulmonar cu aspect CT în limite normale.
	Arbore traheo-bronșic permeabil.
	Absenta adenopatiilor mediastinale.
	Absența revărsatelor lichidiene pleuro-pericardice.
	Vase mediastinale permeabile, cu calibru normal.

ABDOMINO-PELVIN:
	Ficat cu dimensiuni in limite normale / crescute (diametru antero-posterior al lobului drept de  mm, al lobului stâng de  mm), structura omogena.
	Căi biliare intra- si extrahepatice nedilatate.
	Colecist destins, cu conținut lichidian, perete cu grosime normală, fără calculi radioopaci.
	Sistem port cu calibru normal, permeabil (venă portă cu diametru de mm, mezenterică superioară mm, splenica de mm).
	Pancreas, glande suprarenale cu aspect CT în limite normale.
	Splină cu dimensiuni de ~  mm (diametru maxim axial x grosime x lungime cranio-caudală), cu structură omogenă.
	Rinichi cu poziție, dimensiuni și morfologie normale, cu secreție și excreție simetrice, în limite normale de timp.
	Nu se constată adenopatii abdomino-pelvine.
	Nu se identifică lichid intraperitoneal.
	Colon cu pregătire digestivă, a fost destins prin administrare de contrast neutru (apă) prin clismă. În condițiile de examinare nu se constată modificări parietale, de calibru sau formațiuni expansive intraluminale.
	Stomac, anse intestinale destinse prin administrare de contrast neutru (apa), fara prize de contrast parietale patologice.
	Vezică urinară, vezicule seminale cu aspect CT în limite normale.
	Prostată cu dimensiuni de ~ (diametru antero-posterior x transversal x cranio-caudal), cu aspect CT normal.
	Vezica urinara, cu aspect CT normal.
	Uter cu dimensiuni de ~  mm (diametru longitudinal x diametre transversale), cu aspect normal. Endometru cu grosimea de  mm. Ovar drept cu dimensiuni de ~  (diametru antero-posterior x transversal x cranio-caudal) și stâng cu dimensiuni de  ~  (diametru antero-posterior x transversal x cranio-caudal), cu aspect CT normal.
	Modificari degenerativ-artrozice la nivelul coloanei vertebrale cuprinse in volumul scanat, manifestate prin osteofite antero-marginale, cu formare de punti osteofitice, fenomene de vid discal, pensarea spatiilor.
	Baze pulmonare cu aspect în limite normale."
        });

        _protocols.Add(new ClinicalProtocol
        {
            Id = "URG_CT_TRAUMA_CRANIO",
            Title = "URG CT TRAUMA CRANIO-CEREBRAL",
            Category = "CT",
            BodyRegion = "Trauma Cranio-Cerebral",
            DefaultContrast = "Nu",
            Content =
@"CRANIO-CEREBRAL:
	Cisternele de la baza sunt libere.
	Trunchi cerebral, fosa posterioara, sistem ventricular, emisfere cerebrale cu aspect CT normal.
	Fara elemente de semiologie CT sugestive pentru hemoragie subarahnoidiana, hematoame intra- sau extranevraxiale, accident vascular ischemic.
	Structurile liniei mediene situate in planul de simetrie al capului.
	Nu se deceleaza traiecte de fractura la nivelul neuro- si viscerocraniului."
        });

        _protocols.Add(new ClinicalProtocol
        {
            Id = "URG_CT_TRAUMA_COLOANA_CERVICALA",
            Title = "URG CT TRAUMA COLOANA CERVICALA",
            Category = "CT",
            BodyRegion = "Trauma Coloană Cervicală",
            DefaultContrast = "Nu",
            Content =
@"COLOANA CERVICALA:
	Nu se constata traiecte de fractura la nivelul vertebrelor cervicale.
	Congruenta articulatiilor atlanto-occipitale, atlanto-axiale si interapofizare la nivel cervical.
	Aliniament vertebral cervical pastrat, fara tasari vertebrale recente sau deplasari in plan sagital."
        });

        _protocols.Add(new ClinicalProtocol
        {
            Id = "URG_CT_TRAUMA_TORACE",
            Title = "URG CT TRAUMA TORACE",
            Category = "CT",
            BodyRegion = "Trauma Torace",
            DefaultContrast = "Nu",
            Content =
@"TORACE:
	Nu se constata traiecte de fractura la nivelul vertebrelor toracale.
	Nu se constata traiecte de fractura la nivelul arcurilor costale sau la nivelul sternului.
	Absenta pneumotoraxului si a pneumomediastinului.
	Absenta epansamentelor pleuro-pericardice.
	Absenta adenopatiilor mediastinale.
	Parenchim pulmonar fara contuzii sau focare de condensare post-traumatica decelabile CT."
        });

        _protocols.Add(new ClinicalProtocol
        {
            Id = "URG_CT_TRAUMA_ABDOMINOPELVIN",
            Title = "URG CT TRAUMA ABDOMINO-PELVIN",
            Category = "CT",
            BodyRegion = "Trauma Abdomino-Pelvin",
            DefaultContrast = "Nu",
            Content =
@"ABDOMINO-PELVIN:
	Nu se constata traiecte de fractura la nivelul vertebrelor lombare si la nivelul bazinului.
	Nu se identifică lichid intraperitoneal. Absenta pneumoperitoneului.
	Ficat cu dimensiuni normale / usor crescute (diametrul antero-posterior al lobului hepatic drept ~  mm, lob hepatic stang ~  mm), cu structura omogena, fara leziuni focale sau laceratii.
	Sistem venos port permeabil, nedilatat.
	Colecist cu continut lichidian, fara calculi radioopaci. Caile biliare intra si extrahepatice nedilatate.
	Pancreas, splina, rinichi si glande suprarenale cu aspect CT normal, fara modificari traumatice decelabile.
	Absenta adenopatiilor abdominale.
	Vezica urinara cu aspect CT in limite normale."
        });

        _protocols.Add(new ClinicalProtocol
        {
            Id = "URG_CT_TRAUMA_COMPLET",
            Title = "URG CT TRAUMA COMPLET",
            Category = "CT",
            BodyRegion = "Traumă Completă (CC + Coloană + TAP)",
            DefaultContrast = "Da",
            Content =
@"CRANIO-CEREBRAL:
	Cisternele de la baza sunt libere.
	Trunchi cerebral, fosa posterioara, sistem ventricular, emisfere cerebrale cu aspect CT normal.
	Fara elemente de semiologie CT sugestive pentru hemoragie subarahnoidiana, hematoame intra- sau extranevraxiale, accident vascular ischemic.
	Structurile liniei mediene situate in planul de simetrie al capului.
	Nu se deceleaza traiecte de fractura la nivelul neuro- si viscerocraniului.

COLOANA CERVICALA:
	Nu se constata traiecte de fractura la nivelul vertebrelor cervicale.
	Congruenta articulatiilor atlanto-occipitale, atlanto-axiale si interapofizare la nivel cervical.

TORACE:
	Nu se constata traiecte de fractura la nivelul vertebrelor toracale.
	Nu se constata traiecte de fractura la nivelul arcurilor costale sau la nivelul sternului.
	Absenta pneumotoraxului si a pneumomediastinului.
	Absenta epansamentelor pleuro-pericardice.
	Absenta adenopatiilor mediastinale.

ABDOMINO-PELVIN:
	Nu se constata traiecte de fractura la nivelul vertebrelor lombare si la nivelul bazinului.
	Nu se identifică lichid intraperitoneal. Absenta pneumoperitoneului.
	Ficat cu dimensiuni normale / crescute (diametrul antero-posterior al lobului hepatic drept ~  mm, lob hepatic stang ~  mm), cu structura omogena, fara leziuni focale.
	Sistem venos port permeabil, nedilatat.
	Colecist cu continut lichidian, fara calculi radioopaci. Caile biliare intra si extrahepatice nedilatate.
	Pancreas, splina, rinichi si glande suprarenale cu aspect CT normal.
	Absenta adenopatiilor abdominale.
	Vezica urinara cu aspect CT in limite normale."
        });
    }

    private static string[]? _cachedClassicNotesLines;
    private static string[]? _cachedUrgNotesLines;
    private static readonly object _notesLock = new();

    public static (string LastName, string FirstName, string Cnp, string Age, string ExamZone, string Contrast, string ExamDate, string Category) GetPatientDemographics(
        PatientModel? patient, StudyModel? study, SeriesModel? series, ClinicalProtocol? protocol)
    {
        string rawName = patient?.Name ?? series?.PatientName ?? "";
        var (parsedLast, parsedFirst) = ParseDicomName(rawName);

        string lastName = patient != null && !string.IsNullOrWhiteSpace(patient.LastName) && patient.LastName != "-" && patient.LastName != "Anonymous"
            ? patient.LastName
            : parsedLast;
        string firstName = patient != null && !string.IsNullOrWhiteSpace(patient.FirstName) && patient.FirstName != "-"
            ? patient.FirstName
            : parsedFirst;

        string cnp = patient != null && !string.IsNullOrWhiteSpace(patient.Cnp) && patient.Cnp != "-"
            ? patient.Cnp
            : (series?.PatientId ?? "");

        string age = " ani";
        if (patient != null && !string.IsNullOrWhiteSpace(patient.RawAgeOnly) && patient.RawAgeOnly != "-")
        {
            age = $"{patient.RawAgeOnly} ani";
        }
        else if (!string.IsNullOrWhiteSpace(study?.PatientAge))
        {
            age = FormatAge(study.PatientAge);
        }

        string rawDate = !string.IsNullOrWhiteSpace(study?.StudyDate)
            ? study.StudyDate
            : (!string.IsNullOrWhiteSpace(series?.StudyDate) ? series.StudyDate : "");
        string examDate = FormatDate(rawDate);

        string category = protocol?.Category ?? (series?.Modality?.Equals("CT", StringComparison.OrdinalIgnoreCase) == true ? "CT" : "IRM");

        // Automatically determine the examination zone from the protocol or study description
        string region = protocol?.BodyRegion?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(region) && !string.IsNullOrWhiteSpace(study?.StudyDescription))
        {
            region = study.StudyDescription.Trim();
        }
        if (string.IsNullOrWhiteSpace(region) && !string.IsNullOrWhiteSpace(series?.SeriesDescription))
        {
            region = series.SeriesDescription.Trim();
        }

        string examZone;
        if (!string.IsNullOrWhiteSpace(region))
        {
            string upperRegion = region.ToUpperInvariant();
            if (upperRegion.Contains("TEP"))
            {
                examZone = "ANGIO-CT TORACE";
            }
            else if (upperRegion.StartsWith(category, StringComparison.OrdinalIgnoreCase))
            {
                examZone = upperRegion;
            }
            else
            {
                examZone = $"{category} {upperRegion}";
            }
        }
        else
        {
            examZone = category;
        }

        string contrast = protocol?.DefaultContrast ?? "Nu";
        if (examZone.Contains("ANGIO-CT") || examZone.Contains("TEP"))
        {
            contrast = "Da";
        }

        // Check if study/series indicates contrast administration
        if (study != null && study.Series.Any(s =>
            (s.SeriesDescription?.Contains("+C", StringComparison.OrdinalIgnoreCase) ?? false) ||
            (s.SeriesDescription?.Contains("CIV", StringComparison.OrdinalIgnoreCase) ?? false) ||
            (s.SeriesDescription?.Contains("GD", StringComparison.OrdinalIgnoreCase) ?? false) ||
            (s.SeriesDescription?.Contains("CONTRAST", StringComparison.OrdinalIgnoreCase) ?? false)))
        {
            contrast = "Da";
        }

        return (lastName, firstName, cnp, age, examZone, contrast, examDate, category);
    }

    /// <summary>
    /// Resolves the examination name(s) (e.g. for "EXAMEN REALIZAT:").
    /// If multiple examinations are inserted in the report text, returns all of them joined by ". "
    /// (e.g. "CT CRANIO-CEREBRAL. CT TORACE").
    /// Automatically converts CT TEP / TEP to "ANGIO-CT TORACE".
    /// </summary>
    public static string ResolveExaminationName(string reportText, ClinicalProtocol? selectedProtocol, string? fallbackExamZone = null)
    {
        var detectedList = new List<string>();

        if (!string.IsNullOrWhiteSpace(reportText))
        {
            var lines = reportText.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var rawLine in lines)
            {
                string line = rawLine.Trim();
                if (string.IsNullOrEmpty(line)) continue;

                // Match known section headers / protocol indicators at start of lines
                string? exam = null;

                if (line.StartsWith("CRANIO-CEREBRAL:", StringComparison.OrdinalIgnoreCase) ||
                    line.StartsWith("CRANIOCEREBRAL:", StringComparison.OrdinalIgnoreCase) ||
                    line.StartsWith("CT CRANIO-CEREBRAL", StringComparison.OrdinalIgnoreCase) ||
                    line.StartsWith("CT CRANIOCEREBRAL", StringComparison.OrdinalIgnoreCase))
                {
                    exam = "CT CRANIO-CEREBRAL";
                }
                else if (line.StartsWith("TEP:", StringComparison.OrdinalIgnoreCase) ||
                         line.StartsWith("TEP (", StringComparison.OrdinalIgnoreCase) ||
                         line.StartsWith("ANGIO-CT TEP", StringComparison.OrdinalIgnoreCase) ||
                         line.StartsWith("CT TEP", StringComparison.OrdinalIgnoreCase))
                {
                    exam = "ANGIO-CT TORACE";
                }
                else if (line.StartsWith("TORACE:", StringComparison.OrdinalIgnoreCase) ||
                         line.StartsWith("CT TORACE", StringComparison.OrdinalIgnoreCase))
                {
                    exam = "CT TORACE";
                }
                else if (line.StartsWith("ABDOMINO-PELVIN:", StringComparison.OrdinalIgnoreCase) ||
                         line.StartsWith("ABDOMINOPELVIN:", StringComparison.OrdinalIgnoreCase) ||
                         line.StartsWith("CT ABDOMINO-PELVIN", StringComparison.OrdinalIgnoreCase) ||
                         line.StartsWith("ABDOMEN-PELVIS:", StringComparison.OrdinalIgnoreCase) ||
                         line.StartsWith("ABDOMEN SI PELVIS:", StringComparison.OrdinalIgnoreCase))
                {
                    exam = "CT ABDOMINO-PELVIN";
                }
                else if (line.StartsWith("COLOANA CERVICALA:", StringComparison.OrdinalIgnoreCase) ||
                         line.StartsWith("CT COLOANA CERVICALA", StringComparison.OrdinalIgnoreCase))
                {
                    exam = "CT COLOANA CERVICALA";
                }
                else if (line.StartsWith("COLOANA TORACALA:", StringComparison.OrdinalIgnoreCase) ||
                         line.StartsWith("COLOANA DORSALA:", StringComparison.OrdinalIgnoreCase) ||
                         line.StartsWith("CT COLOANA TORACALA", StringComparison.OrdinalIgnoreCase) ||
                         line.StartsWith("CT COLOANA DORSALA", StringComparison.OrdinalIgnoreCase))
                {
                    exam = "CT COLOANA TORACALA";
                }
                else if (line.StartsWith("COLOANA LOMBARA:", StringComparison.OrdinalIgnoreCase) ||
                         line.StartsWith("CT COLOANA LOMBARA", StringComparison.OrdinalIgnoreCase))
                {
                    exam = "CT COLOANA LOMBARA";
                }
                else if (line.StartsWith("CT UMAR", StringComparison.OrdinalIgnoreCase) ||
                         line.StartsWith("CT UMĂR", StringComparison.OrdinalIgnoreCase) ||
                         line.StartsWith("UMAR:", StringComparison.OrdinalIgnoreCase) ||
                         line.StartsWith("UMĂR:", StringComparison.OrdinalIgnoreCase))
                {
                    exam = "CT UMAR";
                }
                else if (line.StartsWith("CT COT", StringComparison.OrdinalIgnoreCase) ||
                         line.StartsWith("COT:", StringComparison.OrdinalIgnoreCase))
                {
                    exam = "CT COT";
                }
                else if (line.StartsWith("CT PUMN", StringComparison.OrdinalIgnoreCase) ||
                         line.StartsWith("PUMN:", StringComparison.OrdinalIgnoreCase))
                {
                    exam = "CT PUMN";
                }
                else if (line.StartsWith("CT BAZIN", StringComparison.OrdinalIgnoreCase) ||
                         line.StartsWith("BAZIN:", StringComparison.OrdinalIgnoreCase))
                {
                    exam = "CT BAZIN";
                }
                else if (line.StartsWith("CT GENUNCHI", StringComparison.OrdinalIgnoreCase) ||
                         line.StartsWith("GENUNCHI:", StringComparison.OrdinalIgnoreCase))
                {
                    exam = "CT GENUNCHI";
                }
                else if (line.StartsWith("CT COAPSA", StringComparison.OrdinalIgnoreCase) ||
                         line.StartsWith("CT COAPSĂ", StringComparison.OrdinalIgnoreCase) ||
                         line.StartsWith("COAPSA:", StringComparison.OrdinalIgnoreCase) ||
                         line.StartsWith("COAPSĂ:", StringComparison.OrdinalIgnoreCase))
                {
                    exam = "CT COAPSA";
                }
                else if (line.StartsWith("CT GAMBA", StringComparison.OrdinalIgnoreCase) ||
                         line.StartsWith("CT GAMBĂ", StringComparison.OrdinalIgnoreCase) ||
                         line.StartsWith("GAMBA:", StringComparison.OrdinalIgnoreCase) ||
                         line.StartsWith("GAMBĂ:", StringComparison.OrdinalIgnoreCase))
                {
                    exam = "CT GAMBA";
                }
                else if (line.StartsWith("CT GLEZNA", StringComparison.OrdinalIgnoreCase) ||
                         line.StartsWith("CT GLEZNĂ", StringComparison.OrdinalIgnoreCase) ||
                         line.StartsWith("GLEZNA:", StringComparison.OrdinalIgnoreCase) ||
                         line.StartsWith("GLEZNĂ:", StringComparison.OrdinalIgnoreCase))
                {
                    exam = "CT GLEZNA";
                }
                else if (line.StartsWith("CT ANTEPICIOR", StringComparison.OrdinalIgnoreCase) ||
                         line.StartsWith("ANTEPICIOR:", StringComparison.OrdinalIgnoreCase))
                {
                    exam = "CT ANTEPICIOR";
                }
                else if (line.StartsWith("CT GAT", StringComparison.OrdinalIgnoreCase) ||
                         line.StartsWith("CT GÂT", StringComparison.OrdinalIgnoreCase) ||
                         line.StartsWith("GAT:", StringComparison.OrdinalIgnoreCase) ||
                         line.StartsWith("GÂT:", StringComparison.OrdinalIgnoreCase))
                {
                    exam = "CT GAT";
                }

                if (exam != null && !detectedList.Contains(exam))
                {
                    detectedList.Add(exam);
                }
            }
        }

        // Handle combined templates if user explicitly chose TAP or TRAUMA COMPLET
        if (selectedProtocol != null)
        {
            if (selectedProtocol.Id == "URG_CT_TAP")
            {
                // If only Torace and Abdomino-Pelvin detected, represent as CT TAP
                if (detectedList.Count == 2 && detectedList.Contains("CT TORACE") && detectedList.Contains("CT ABDOMINO-PELVIN"))
                {
                    return "CT TAP";
                }
                else if (detectedList.Contains("CT TORACE") && detectedList.Contains("CT ABDOMINO-PELVIN"))
                {
                    int torIdx = detectedList.IndexOf("CT TORACE");
                    detectedList.Remove("CT TORACE");
                    int abdIdx = detectedList.IndexOf("CT ABDOMINO-PELVIN");
                    if (abdIdx >= 0) detectedList.RemoveAt(abdIdx);
                    detectedList.Insert(Math.Min(torIdx, detectedList.Count), "CT TAP");
                }
            }
            else if (selectedProtocol.Id == "URG_CT_TRAUMA_COMPLET")
            {
                if (detectedList.Count >= 3 && detectedList.Contains("CT CRANIO-CEREBRAL") && detectedList.Contains("CT TORACE"))
                {
                    return "CT TRAUMA COMPLET";
                }
            }
        }

        if (detectedList.Count > 0)
        {
            return string.Join(". ", detectedList);
        }

        // Fallback to selected protocol
        if (selectedProtocol != null && !string.IsNullOrWhiteSpace(selectedProtocol.Title))
        {
            string title = selectedProtocol.Title.StartsWith("URG ", StringComparison.OrdinalIgnoreCase)
                ? selectedProtocol.Title.Substring(4).Trim()
                : selectedProtocol.Title.Trim();

            if (title.IndexOf("TEP", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "ANGIO-CT TORACE";
            }
            return title.ToUpperInvariant();
        }

        // Fallback to exam zone from DICOM / demographics
        if (!string.IsNullOrWhiteSpace(fallbackExamZone))
        {
            if (fallbackExamZone.IndexOf("TEP", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "ANGIO-CT TORACE";
            }
            return fallbackExamZone.ToUpperInvariant();
        }

        return "CT";
    }

    /// <summary>
    /// Generates the template content for the editor textbox (strictly TEXT + CONCLUZII, without header/footer).
    /// Header and footer are added automatically by Parsec when exporting/saving as .docx.
    /// </summary>
    public static string GenerateFullReport(ClinicalProtocol protocol, PatientModel? patient, StudyModel? study, SeriesModel? series)
    {
        var sb = new StringBuilder();
        sb.AppendLine(protocol.Content.Trim());
        if (!protocol.Id.StartsWith("URG_", StringComparison.OrdinalIgnoreCase) &&
            !protocol.Content.Contains("CONCLUZII:", StringComparison.OrdinalIgnoreCase) &&
            !protocol.Content.Contains("Concluzii:", StringComparison.OrdinalIgnoreCase))
        {
            sb.AppendLine();
            sb.AppendLine("CONCLUZII:");
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Wraps the radiological interpretation and conclusions from the editor with the patient demographic header,
    /// centered REZULTAT EXAMEN IRM/CT title, and DATA / MEDIC EXAMINATOR footer for .docx export.
    /// </summary>
    public static string BuildCompleteReportForExport(
        string interpretationAndConclusions,
        ClinicalProtocol? protocol,
        PatientModel? patient,
        StudyModel? study,
        SeriesModel? series)
    {
        string cleanBody = StripHeaderAndFooter(interpretationAndConclusions);

        bool isCt = (series?.Modality?.Equals("CT", StringComparison.OrdinalIgnoreCase) == true) ||
                    (study?.Series.Any(s => s.Modality.Equals("CT", StringComparison.OrdinalIgnoreCase)) == true) ||
                    cleanBody.Contains("decelabile ct", StringComparison.OrdinalIgnoreCase) ||
                    cleanBody.Contains("aspect ct", StringComparison.OrdinalIgnoreCase);

        // If the caller provided a valid selected protocol matching the examination modality and non-empty body, respect it 100%;
        // when saving a blank report (empty body), allow DetectBestProtocol to infer the exam zone from DICOM Study/Series description first.
        ClinicalProtocol effectiveProtocol;
        if (!string.IsNullOrWhiteSpace(cleanBody) && protocol != null && (!isCt || protocol.Category == "CT") && (isCt || protocol.Category == "IRM"))
        {
            effectiveProtocol = protocol;
        }
        else
        {
            effectiveProtocol = DetectBestProtocol(cleanBody, study, series, protocol);
        }

        var demo = GetPatientDemographics(patient, study, series, effectiveProtocol);
        string resolvedZone = ResolveExaminationName(cleanBody, effectiveProtocol, demo.ExamZone);
        string contrast = (demo.Contrast == "Da" || resolvedZone.Contains("ANGIO-CT")) ? "Da" : demo.Contrast;

        var sb = new StringBuilder();
        sb.AppendLine($"Nume: {demo.LastName}\tCNP: {demo.Cnp}");
        sb.AppendLine($"Prenume: {demo.FirstName}\tVârsta: {demo.Age}");
        sb.AppendLine($"Zona examinată: {resolvedZone}\tContrast: {contrast}");
        if (demo.Category == "CT")
        {
            double dlpVal = study?.GetOrResolveTotalDlp() ?? 0.0;
            string dlp = dlpVal > 0.0 ? $"{dlpVal:F2} mGycm" : "... mGycm";
            sb.AppendLine($"DLP: {dlp}");
        }
        sb.AppendLine();
        sb.AppendLine($"REZULTAT EXAMEN {demo.Category}");
        sb.AppendLine();
        sb.AppendLine(cleanBody);
        sb.AppendLine();
        sb.AppendLine("\tDATA\tMEDIC EXAMINATOR");
        sb.AppendLine($"\t{demo.ExamDate}\tDr. ");

        return sb.ToString();
    }

    /// <summary>
    /// Ensures the text contains strictly the radiological interpretation and conclusions (TEXT + CONCLUZII),
    /// stripping any patient header, REZULTAT EXAMEN title, or DATA / MEDIC EXAMINATOR footer if present.
    /// </summary>
    public static string StripHeaderAndFooter(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;

        var lines = text.Replace("\r\n", "\n").Split('\n').ToList();

        // Remove leading blank lines, demographic header lines, and REZULTAT EXAMEN line
        while (lines.Count > 0)
        {
            string t = lines[0].Trim();
            if (string.IsNullOrWhiteSpace(t) ||
                t.StartsWith("Nume:", StringComparison.OrdinalIgnoreCase) ||
                t.StartsWith("Prenume:", StringComparison.OrdinalIgnoreCase) ||
                t.StartsWith("Zona examinată:", StringComparison.OrdinalIgnoreCase) ||
                t.StartsWith("Zona examinata:", StringComparison.OrdinalIgnoreCase) ||
                t.StartsWith("DLP:", StringComparison.OrdinalIgnoreCase) ||
                t.StartsWith("REZULTAT EXAMEN", StringComparison.OrdinalIgnoreCase))
            {
                lines.RemoveAt(0);
            }
            else
            {
                break;
            }
        }

        // Remove trailing blank lines and DATA / MEDIC EXAMINATOR footer lines
        while (lines.Count > 0)
        {
            string t = lines[^1].Trim();
            if (string.IsNullOrWhiteSpace(t) ||
                t.StartsWith("DATA", StringComparison.OrdinalIgnoreCase) ||
                t.Contains("MEDIC EXAMINATOR", StringComparison.OrdinalIgnoreCase) ||
                (t.Contains("Dr.", StringComparison.OrdinalIgnoreCase) && t.Length <= 65) ||
                System.Text.RegularExpressions.Regex.IsMatch(t, @"^\d{2}\.\d{2}\.\d{4}(\s+|$)"))
            {
                lines.RemoveAt(lines.Count - 1);
            }
            else
            {
                break;
            }
        }

        return string.Join(Environment.NewLine, lines).Trim();
    }

    public static string GetProtocolInsertText(ClinicalProtocol protocol)
    {
        return "\n" + protocol.Content.Trim() + "\n";
    }

    /// <summary>
    /// Automatically detects the best matching ClinicalProtocol based on the dictated/report text and current DICOM study/series.
    /// </summary>
    public static ClinicalProtocol DetectBestProtocol(string dictatedText, StudyModel? study, SeriesModel? series, ClinicalProtocol? currentProtocol)
    {
        string combined = $"{dictatedText} {study?.StudyDescription} {series?.SeriesDescription} {series?.BodyPart}".ToLowerInvariant();
        bool isCt = (series?.Modality?.Equals("CT", StringComparison.OrdinalIgnoreCase) == true) ||
                    (study?.Series.Any(s => s.Modality.Equals("CT", StringComparison.OrdinalIgnoreCase)) == true) ||
                    combined.Contains("examen ct") || combined.Contains("aspect ct") || combined.Contains("decelabile ct") ||
                    combined.Contains("mgy") || combined.Contains(" hounsfield");

        string dictLower = (dictatedText ?? string.Empty).ToLowerInvariant();

        // If current protocol is an emergency protocol (URG), respect user selection 100%
        if (currentProtocol != null && currentProtocol.Id.StartsWith("URG_", StringComparison.OrdinalIgnoreCase))
        {
            return currentProtocol;
        }

        // If current protocol is already set, matches the scan modality, and matches the organ, preserve it!
        if (currentProtocol != null)
        {
            if (isCt && currentProtocol.Category == "CT")
            {
                if (currentProtocol.Id == "CT_ABDOMINOPELVIN" &&
                    (dictLower.Contains("ficat") || dictLower.Contains("rinichi") || dictLower.Contains("ureter") ||
                     dictLower.Contains("calcul") || dictLower.Contains("abdom") || dictLower.Contains("pelv") ||
                     dictLower.Contains("vezic") || dictLower.Contains("pancreas") || dictLower.Contains("splin")))
                {
                    return currentProtocol;
                }
                if (currentProtocol.Id == "CT_TORACE" &&
                    (dictLower.Contains("pulmonar") || dictLower.Contains("mediastin") || dictLower.Contains("pleur")))
                {
                    return currentProtocol;
                }
                if (currentProtocol.Id == "CT_CRANIO" &&
                    (dictLower.Contains("cranio") || dictLower.Contains("cerebral") || dictLower.Contains("creier")))
                {
                    return currentProtocol;
                }
            }
            else if (!isCt && currentProtocol.Category == "IRM")
            {
                if (currentProtocol.Id == "IRM_LOMBAR" && (dictLower.Contains("l1-") || dictLower.Contains("l4-l5") || dictLower.Contains("l5-s1") || dictLower.Contains("con medular")))
                    return currentProtocol;
                if (currentProtocol.Id == "IRM_CERVICAL" && (dictLower.Contains("cervical") || dictLower.Contains("c5-c6")))
                    return currentProtocol;
                if (currentProtocol.Id == "IRM_GENUNCHI" && (dictLower.Contains("genunchi") || dictLower.Contains("menisc")))
                    return currentProtocol;
            }
        }

        string trimmedText = (dictatedText ?? string.Empty).TrimStart();

        // 0. Check explicit URG protocol match (if text starts with URG or emergency title)
        if (trimmedText.StartsWith("URG", StringComparison.OrdinalIgnoreCase))
        {
            var matchUrg = _protocols.FirstOrDefault(p => p.Id.StartsWith("URG_") && 
                (trimmedText.StartsWith(p.Title, StringComparison.OrdinalIgnoreCase) ||
                 trimmedText.StartsWith(p.Title.Replace("URG ", ""), StringComparison.OrdinalIgnoreCase)));
            if (matchUrg != null) return matchUrg;
        }

        // 0. Check explicit protocol header at the start of the report text (highest priority for formatted reports)
        if (trimmedText.StartsWith("CT UMAR", StringComparison.OrdinalIgnoreCase) || trimmedText.StartsWith("CT UMĂR", StringComparison.OrdinalIgnoreCase) || trimmedText.StartsWith("UMAR", StringComparison.OrdinalIgnoreCase) || trimmedText.StartsWith("UMĂR", StringComparison.OrdinalIgnoreCase))
            return _protocols.FirstOrDefault(p => p.Id == "CT_UMAR") ?? currentProtocol ?? _protocols[0];
        if (trimmedText.StartsWith("CT PUMN", StringComparison.OrdinalIgnoreCase) || trimmedText.StartsWith("PUMN", StringComparison.OrdinalIgnoreCase))
            return _protocols.FirstOrDefault(p => p.Id == "CT_PUMN") ?? currentProtocol ?? _protocols[0];
        if (trimmedText.StartsWith("CT BAZIN", StringComparison.OrdinalIgnoreCase))
            return _protocols.FirstOrDefault(p => p.Id == "CT_BAZIN") ?? currentProtocol ?? _protocols[0];
        if (trimmedText.StartsWith("CT COAPSA", StringComparison.OrdinalIgnoreCase) || trimmedText.StartsWith("CT COAPSĂ", StringComparison.OrdinalIgnoreCase) || trimmedText.StartsWith("COAPSA", StringComparison.OrdinalIgnoreCase) || trimmedText.StartsWith("COAPSĂ", StringComparison.OrdinalIgnoreCase))
            return _protocols.FirstOrDefault(p => p.Id == "CT_COAPSA") ?? currentProtocol ?? _protocols[0];
        if (trimmedText.StartsWith("CT GAMBA", StringComparison.OrdinalIgnoreCase) || trimmedText.StartsWith("CT GAMBĂ", StringComparison.OrdinalIgnoreCase) || trimmedText.StartsWith("GAMBA", StringComparison.OrdinalIgnoreCase) || trimmedText.StartsWith("GAMBĂ", StringComparison.OrdinalIgnoreCase))
            return _protocols.FirstOrDefault(p => p.Id == "CT_GAMBA") ?? currentProtocol ?? _protocols[0];
        if (trimmedText.StartsWith("CT ANTEPICIOR", StringComparison.OrdinalIgnoreCase) || trimmedText.StartsWith("ANTEPICIOR", StringComparison.OrdinalIgnoreCase))
            return _protocols.FirstOrDefault(p => p.Id == "CT_ANTEPICIOR") ?? currentProtocol ?? _protocols[0];
        if (trimmedText.StartsWith("ABDOMINO-PELVIN", StringComparison.OrdinalIgnoreCase))
            return _protocols.FirstOrDefault(p => p.Id == "CT_ABDOMINOPELVIN") ?? currentProtocol ?? _protocols[0];
        if (trimmedText.StartsWith("COLOANA LOMBARA", StringComparison.OrdinalIgnoreCase) || trimmedText.StartsWith("COLOANĂ LOMBARĂ", StringComparison.OrdinalIgnoreCase))
            return _protocols.FirstOrDefault(p => p.Id == (isCt ? "CT_COLOANA_LOMBARA" : "IRM_LOMBAR")) ?? currentProtocol ?? _protocols[0];
        if (trimmedText.StartsWith("COLOANA CERVICALA", StringComparison.OrdinalIgnoreCase) || trimmedText.StartsWith("COLOANĂ CERVICALĂ", StringComparison.OrdinalIgnoreCase))
            return _protocols.FirstOrDefault(p => p.Id == (isCt ? "CT_COLOANA_CERVICALA" : "IRM_CERVICAL")) ?? currentProtocol ?? _protocols[0];
        if (trimmedText.StartsWith("COLOANA TORACALA", StringComparison.OrdinalIgnoreCase) || trimmedText.StartsWith("COLOANĂ TORACALĂ", StringComparison.OrdinalIgnoreCase))
            return _protocols.FirstOrDefault(p => p.Id == (isCt ? "CT_COLOANA_TORACALA" : "IRM_TORACAL")) ?? currentProtocol ?? _protocols[0];
        if (trimmedText.StartsWith("CRANIOCEREBRAL", StringComparison.OrdinalIgnoreCase))
            return _protocols.FirstOrDefault(p => p.Id == (isCt ? "CT_CRANIO" : "IRM_CRANIO")) ?? currentProtocol ?? _protocols[0];
        if (trimmedText.StartsWith("CRANIO-CEREBRAL", StringComparison.OrdinalIgnoreCase))
            return _protocols.FirstOrDefault(p => p.Id == (isCt ? "CT_CRANIO" : "IRM_CRANIO")) ?? currentProtocol ?? _protocols[0];
        if (trimmedText.StartsWith("TORACE", StringComparison.OrdinalIgnoreCase))
            return _protocols.FirstOrDefault(p => p.Id == "CT_TORACE") ?? currentProtocol ?? _protocols[0];
        if (trimmedText.StartsWith("TEP", StringComparison.OrdinalIgnoreCase))
            return _protocols.FirstOrDefault(p => p.Id == "CT_TEP") ?? currentProtocol ?? _protocols[0];
        if (trimmedText.StartsWith("CT GENUNCHI", StringComparison.OrdinalIgnoreCase))
            return _protocols.FirstOrDefault(p => p.Id == "CT_GENUNCHI") ?? currentProtocol ?? _protocols[0];
        if (trimmedText.StartsWith("GENUNCHI", StringComparison.OrdinalIgnoreCase))
            return _protocols.FirstOrDefault(p => p.Id == (isCt ? "CT_GENUNCHI" : "IRM_GENUNCHI")) ?? currentProtocol ?? _protocols[0];
        if (trimmedText.StartsWith("CT GLEZNA", StringComparison.OrdinalIgnoreCase) || trimmedText.StartsWith("CT GLEZNĂ", StringComparison.OrdinalIgnoreCase) || trimmedText.StartsWith("GLEZNA", StringComparison.OrdinalIgnoreCase) || trimmedText.StartsWith("GLEZNĂ", StringComparison.OrdinalIgnoreCase))
            return _protocols.FirstOrDefault(p => p.Id == "CT_GLEZNA") ?? currentProtocol ?? _protocols[0];
        if (trimmedText.StartsWith("CT COT", StringComparison.OrdinalIgnoreCase) || trimmedText.StartsWith("COT", StringComparison.OrdinalIgnoreCase))
            return _protocols.FirstOrDefault(p => p.Id == "CT_COT") ?? currentProtocol ?? _protocols[0];
        if (trimmedText.StartsWith("CT GAT", StringComparison.OrdinalIgnoreCase) || trimmedText.StartsWith("CT GÂT", StringComparison.OrdinalIgnoreCase) || trimmedText.StartsWith("GAT", StringComparison.OrdinalIgnoreCase) || trimmedText.StartsWith("GÂT", StringComparison.OrdinalIgnoreCase))
            return _protocols.FirstOrDefault(p => p.Id == "CT_GAT") ?? currentProtocol ?? _protocols[0];
        if (trimmedText.StartsWith("HIPOFIZA", StringComparison.OrdinalIgnoreCase) || trimmedText.StartsWith("HIPOFIZĂ", StringComparison.OrdinalIgnoreCase))
            return _protocols.FirstOrDefault(p => p.Id == "IRM_HIPOFIZA") ?? currentProtocol ?? _protocols[0];
        if (trimmedText.StartsWith("MRCP", StringComparison.OrdinalIgnoreCase))
            return _protocols.FirstOrDefault(p => p.Id == "IRM_MRCP") ?? currentProtocol ?? _protocols[0];
        if (trimmedText.StartsWith("IRM HEPATIC", StringComparison.OrdinalIgnoreCase))
            return _protocols.FirstOrDefault(p => p.Id == "IRM_HEPATIC") ?? currentProtocol ?? _protocols[0];
        if (trimmedText.StartsWith("ENTERO-RM", StringComparison.OrdinalIgnoreCase))
            return _protocols.FirstOrDefault(p => p.Id == "IRM_ENTERO") ?? currentProtocol ?? _protocols[0];
        if (trimmedText.StartsWith("ABDOMEN", StringComparison.OrdinalIgnoreCase))
            return _protocols.FirstOrDefault(p => p.Id == (isCt ? "CT_ABDOMINOPELVIN" : "IRM_ABDOMEN")) ?? currentProtocol ?? _protocols[0];
        if (trimmedText.StartsWith("PELVIS", StringComparison.OrdinalIgnoreCase))
            return _protocols.FirstOrDefault(p => p.Id == (isCt ? "CT_ABDOMINOPELVIN" : "IRM_PELVIS")) ?? currentProtocol ?? _protocols[0];
        if (trimmedText.StartsWith("BAZIN", StringComparison.OrdinalIgnoreCase))
            return _protocols.FirstOrDefault(p => p.Id == "IRM_BAZIN") ?? currentProtocol ?? _protocols[0];

        // 1. Check Abdominal and Pelvic organs FIRST (prevents incidental "ureter lombar" or "coloanei vertebrale" from triggering spine!)
        if (dictLower.Contains("ficat") || dictLower.Contains("colecist") || dictLower.Contains("pancreas") ||
            dictLower.Contains("splin") || dictLower.Contains("rinichi") || dictLower.Contains("ureter") ||
            dictLower.Contains("pielocaliceal") || dictLower.Contains("hidronefroz") || dictLower.Contains("calcul") ||
            dictLower.Contains("vezic") || dictLower.Contains("apendic") || dictLower.Contains("cec") ||
            dictLower.Contains("retroperitone") || dictLower.Contains("intraperitone") ||
            dictLower.Contains("abdomino-pelvin") || dictLower.Contains("abdomin") || dictLower.Contains("pelvin"))
        {
            return _protocols.FirstOrDefault(p => p.Id == (isCt ? "CT_ABDOMINOPELVIN" : "IRM_ABDOMEN")) ?? currentProtocol ?? _protocols[0];
        }

        if (dictLower.Contains("uter") || dictLower.Contains("ovar") || dictLower.Contains("prostat") || dictLower.Contains("vezicule seminale"))
            return _protocols.FirstOrDefault(p => p.Id == (isCt ? "CT_ABDOMINOPELVIN" : "IRM_PELVIS")) ?? currentProtocol ?? _protocols[0];

        // 2. Spine and Musculoskeletal checks (for CT scans, only return if explicit or fallback to CT)
        if (!isCt)
        {
            if (dictLower.Contains("con medular") || dictLower.Contains("l1-l2") || dictLower.Contains("l2-l3") || dictLower.Contains("l3-l4") || dictLower.Contains("l4-l5") || dictLower.Contains("l5-s1") || (dictLower.Contains("lombar") && !dictLower.Contains("ureter")))
                return _protocols.FirstOrDefault(p => p.Id == "IRM_LOMBAR") ?? currentProtocol ?? _protocols[0];

            if (dictLower.Contains("cervical") || dictLower.Contains("unco-vertebral") || dictLower.Contains("atlanto-odontoid") || dictLower.Contains("c3-c4") || dictLower.Contains("c4-c5") || dictLower.Contains("c5-c6") || dictLower.Contains("c6-c7"))
                return _protocols.FirstOrDefault(p => p.Id == "IRM_CERVICAL") ?? currentProtocol ?? _protocols[0];

            if (dictLower.Contains("toracal") || dictLower.Contains("dorsal") || dictLower.Contains("t3-") || dictLower.Contains("t4-") || dictLower.Contains("t7-") || dictLower.Contains("t8-") || dictLower.Contains("t10-") || dictLower.Contains("t11-"))
                return _protocols.FirstOrDefault(p => p.Id == "IRM_TORACAL") ?? currentProtocol ?? _protocols[0];

            if (dictLower.Contains("genunchi") || dictLower.Contains("menisc") || dictLower.Contains("patel") || dictLower.Contains("incrucisat") || dictLower.Contains("încrucișat") || dictLower.Contains("condromalacie") || dictLower.Contains("baker"))
                return _protocols.FirstOrDefault(p => p.Id == "IRM_GENUNCHI") ?? currentProtocol ?? _protocols[0];

            if (dictLower.Contains("hipofiz") || dictLower.Contains("selar") || dictLower.Contains("adenohipofiz"))
                return _protocols.FirstOrDefault(p => p.Id == "IRM_HIPOFIZA") ?? currentProtocol ?? _protocols[0];

            if (dictLower.Contains("coxo-femural") || dictLower.Contains("cap femural") || dictLower.Contains("sacro-iliac"))
                return _protocols.FirstOrDefault(p => p.Id == "IRM_BAZIN") ?? currentProtocol ?? _protocols[0];

            if (dictLower.Contains("colangio") || dictLower.Contains("mrcp") || dictLower.Contains("coledoc") || dictLower.Contains("wirsung"))
                return _protocols.FirstOrDefault(p => p.Id == "IRM_MRCP") ?? currentProtocol ?? _protocols[0];
        }

        // 3. Thoracic and Pulmonary checks
        if (dictLower.Contains("tromboembolism") || dictLower.Contains("artera pulmonara") || dictLower.Contains("arteră pulmonară"))
            return _protocols.FirstOrDefault(p => p.Id == "CT_TEP") ?? currentProtocol ?? _protocols[0];

        string dictWithoutBases = dictLower.Replace("baze pulmonare", "").Replace("bazele pulmonare", "");
        if (dictWithoutBases.Contains("pulmonar") || dictWithoutBases.Contains("mediastin") || dictWithoutBases.Contains("pleur") || dictWithoutBases.Contains("bronsiectaz") || dictWithoutBases.Contains("bronșiectaz") || dictWithoutBases.Contains("emfizem"))
            return _protocols.FirstOrDefault(p => p.Id == "CT_TORACE") ?? currentProtocol ?? _protocols[0];

        // 4. Brain / Cranial checks
        if (dictLower.Contains("emisfere cerebrale") || dictLower.Contains("fosa posterioara") || dictLower.Contains("fosă posterioară") || dictLower.Contains("ventricul") || dictLower.Contains("demielinizant") || dictLower.Contains("leucoaraioz"))
            return _protocols.FirstOrDefault(p => p.Id == (isCt ? "CT_CRANIO" : "IRM_CRANIO")) ?? currentProtocol ?? _protocols[0];

        // 5. Fallback to study description / body part matching
        if (combined.Contains("abdom") || combined.Contains("pelv"))
            return _protocols.FirstOrDefault(p => p.Id == (isCt ? "CT_ABDOMINOPELVIN" : "IRM_ABDOMEN")) ?? currentProtocol ?? _protocols[0];
        if (combined.Contains("torac") || combined.Contains("chest") || combined.Contains("thorax"))
            return _protocols.FirstOrDefault(p => p.Id == "CT_TORACE") ?? currentProtocol ?? _protocols[0];
        if (combined.Contains("craniu") || combined.Contains("cerebral") || combined.Contains("head") || combined.Contains("brain"))
            return _protocols.FirstOrDefault(p => p.Id == (isCt ? "CT_CRANIO" : "IRM_CRANIO")) ?? currentProtocol ?? _protocols[0];

        if (!isCt)
        {
            if (combined.Contains("lombar") || combined.Contains("l-spine") || combined.Contains("lspine"))
                return _protocols.FirstOrDefault(p => p.Id == "IRM_LOMBAR") ?? currentProtocol ?? _protocols[0];
            if (combined.Contains("cervical") || combined.Contains("c-spine") || combined.Contains("cspine"))
                return _protocols.FirstOrDefault(p => p.Id == "IRM_CERVICAL") ?? currentProtocol ?? _protocols[0];
            if (combined.Contains("toracal") || combined.Contains("dorsal") || combined.Contains("t-spine") || combined.Contains("tspine"))
                return _protocols.FirstOrDefault(p => p.Id == "IRM_TORACAL") ?? currentProtocol ?? _protocols[0];
            if (combined.Contains("genunchi") || combined.Contains("knee"))
                return _protocols.FirstOrDefault(p => p.Id == "IRM_GENUNCHI") ?? currentProtocol ?? _protocols[0];
        }
        else
        {
            if (combined.Contains("lombar") || combined.Contains("l-spine") || combined.Contains("lspine"))
                return _protocols.FirstOrDefault(p => p.Id == "CT_COLOANA_LOMBARA") ?? currentProtocol ?? _protocols[0];
            if (combined.Contains("cervical") || combined.Contains("c-spine") || combined.Contains("cspine"))
                return _protocols.FirstOrDefault(p => p.Id == "CT_COLOANA_CERVICALA") ?? currentProtocol ?? _protocols[0];
            if (combined.Contains("toracal") || combined.Contains("dorsal") || combined.Contains("t-spine") || combined.Contains("tspine"))
                return _protocols.FirstOrDefault(p => p.Id == "CT_COLOANA_TORACALA") ?? currentProtocol ?? _protocols[0];
            if (combined.Contains("genunchi") || combined.Contains("knee"))
                return _protocols.FirstOrDefault(p => p.Id == "CT_GENUNCHI") ?? currentProtocol ?? _protocols[0];
            if (combined.Contains("glezn") || combined.Contains("ankle") || combined.Contains("picior"))
                return _protocols.FirstOrDefault(p => p.Id == "CT_GLEZNA") ?? currentProtocol ?? _protocols[0];
            if (combined.Contains("cot") || combined.Contains("elbow"))
                return _protocols.FirstOrDefault(p => p.Id == "CT_COT") ?? currentProtocol ?? _protocols[0];
            if (combined.Contains("gat") || combined.Contains("gât") || combined.Contains("neck") || combined.Contains("faring") || combined.Contains("laring") || combined.Contains("tiroid"))
                return _protocols.FirstOrDefault(p => p.Id == "CT_GAT") ?? currentProtocol ?? _protocols[0];
        }

        if (isCt)
        {
            return _protocols.FirstOrDefault(p => p.Id == "CT_ABDOMINOPELVIN") ?? currentProtocol ?? _protocols[0];
        }

        return currentProtocol ?? _protocols[0];
    }

    /// <summary>
    /// Fast (<5ms) in-memory semantic/keyword matcher that extracts the top 25 most relevant clinical sentences
    /// from the doctor's reference notes for the current organ and dictation.
    /// When an emergency protocol (URG) is selected, lines are queried strictly from the emergency files (URGENTE CT.txt and REZULTATELE MELE URGENTE CT .txt).
    /// When a classic protocol is selected, lines are queried strictly from the classic files (modele_rezultate.txt / notepad.txt).
    /// </summary>
    public static string ExtractRelevantReferenceLines(string dictatedText, ClinicalProtocol? protocol, int maxLines = 25)
    {
        try
        {
            bool isUrg = protocol?.Id?.StartsWith("URG_", StringComparison.OrdinalIgnoreCase) == true;
            var lines = isUrg ? GetCachedUrgNotesLines() : GetCachedClassicNotesLines();
            if (lines == null || lines.Length == 0) return string.Empty;

            // Build anatomical region keywords from protocol
            var regionKeywords = GetRegionKeywords(protocol?.Id ?? "");

            // Extract significant medical tokens (>= 4 chars) from dictated text
            var dictTokens = (dictatedText ?? string.Empty)
                .ToLowerInvariant()
                .Split(new[] { ' ', ',', '.', ';', ':', '(', ')', '-', '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries)
                .Where(w => w.Length >= 4 && !StopWords.Contains(w))
                .Distinct()
                .ToArray();

            var scored = new List<(string Line, int Score, int Index)>(256);

            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                if (string.IsNullOrWhiteSpace(line) || line.Length < 12) continue;

                string lower = line.ToLowerInvariant();
                int regionScore = 0;
                foreach (var rk in regionKeywords)
                {
                    if (lower.Contains(rk)) regionScore += 3;
                }

                // Only include lines that belong to the target anatomical region (or strongly match dictated words)
                int dictScore = 0;
                foreach (var dt in dictTokens)
                {
                    if (lower.Contains(dt)) dictScore += 2;
                }

                if (regionScore > 0 || dictScore >= 4)
                {
                    // Bonus if it matches both the organ and specific dictated pathology
                    int totalScore = regionScore + dictScore + (regionScore > 0 && dictScore > 0 ? 5 : 0);
                    scored.Add((line.Trim(), totalScore, i));
                }
            }

            var topLines = scored
                .OrderByDescending(x => x.Score)
                .ThenBy(x => x.Index)
                .Select(x => x.Line)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(maxLines)
                .ToList();

            return string.Join("\n", topLines);
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string[] GetCachedClassicNotesLines()
    {
        if (_cachedClassicNotesLines != null) return _cachedClassicNotesLines;
        lock (_notesLock)
        {
            if (_cachedClassicNotesLines != null) return _cachedClassicNotesLines;

            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string[] candidatePaths = new[]
            {
                System.IO.Path.Combine(appData, "Parsec", "ReferenceReports", "modele_rezultate.txt"),
                System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "modele_rezultate.txt"),
                @"f:\Desktop\notepad.txt"
            };

            foreach (var path in candidatePaths)
            {
                if (System.IO.File.Exists(path))
                {
                    _cachedClassicNotesLines = System.IO.File.ReadAllLines(path, Encoding.UTF8);
                    return _cachedClassicNotesLines;
                }
            }

            _cachedClassicNotesLines = Array.Empty<string>();
            return _cachedClassicNotesLines;
        }
    }

    private static string[] GetCachedUrgNotesLines()
    {
        if (_cachedUrgNotesLines != null) return _cachedUrgNotesLines;
        lock (_notesLock)
        {
            if (_cachedUrgNotesLines != null) return _cachedUrgNotesLines;

            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string baseAssets = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets");
            string refReports = System.IO.Path.Combine(appData, "Parsec", "ReferenceReports");

            var linesList = new List<string>(4096);
            string[] fileNames = new[] { "URGENTE CT.txt", "REZULTATELE MELE URGENTE CT .txt" };

            foreach (var fn in fileNames)
            {
                string[] searchPaths = new[]
                {
                    System.IO.Path.Combine(refReports, fn),
                    System.IO.Path.Combine(baseAssets, fn),
                    System.IO.Path.Combine(@"f:\Desktop", fn)
                };

                foreach (var sp in searchPaths)
                {
                    if (System.IO.File.Exists(sp))
                    {
                        try
                        {
                            var fl = System.IO.File.ReadAllLines(sp, Encoding.UTF8);
                            linesList.AddRange(fl);
                            break;
                        }
                        catch { }
                    }
                }
            }

            _cachedUrgNotesLines = linesList.ToArray();
            return _cachedUrgNotesLines;
        }
    }

    private static string[] GetRegionKeywords(string protocolId)
    {
        return protocolId switch
        {
            "URG_CT_CRANIO" or "URG_CT_TRAUMA_CRANIO" or "IRM_CRANIO" or "CT_CRANIO" => new[] { "emisfere cerebrale", "fosa posterioara", "trunchi cerebral", "ventricul", "hipocamp", "demielinizant", "flair", "leucoaraioza", "cisternele", "liniei mediene", "sinus", "cranio-cerebral", "hsa", "subarahnoidiana", "hematom", "fractura", "neurocraniu", "viscerocraniu" },
            "URG_CT_TORACE" or "URG_CT_TRAUMA_TORACE" or "CT_TORACE" => new[] { "pulmonar", "mediastin", "pleur", "bronsiectaz", "emfizem", "geam mat", "traheo-bronsic", "subpleural", "pneumotorax", "hemotorax", "costal", "stern", "arcurilor costale" },
            "URG_CT_TEP" or "CT_TEP" => new[] { "tep", "artera pulmonara", "arterei pulmonare", "defect de umplere", "trombembolism", "ramurilor lobare", "segmentare", "subsegmentare", "infarct pulmonar" },
            "URG_CT_ABDOMINOPELVIN" or "URG_CT_TRAUMA_ABDOMINOPELVIN" or "CT_ABDOMINOPELVIN" or "IRM_ABDOMEN" or "IRM_HEPATIC" or "IRM_MRCP" => new[] { "ficat", "colecist", "biliar", "pancreas", "splin", "rinichi", "suprarenal", "porta", "coledoc", "abdomin", "apendic", "cec", "pelvis", "vezica urinara", "uter", "ovar", "prostat", "intraperitoneal", "hemoperitoneu", "pneumoperitoneu" },
            "URG_CT_TAP" => new[] { "pulmonar", "mediastin", "pleur", "ficat", "colecist", "pancreas", "splin", "rinichi", "suprarenal", "vezica", "abdomin", "pelvin" },
            "URG_CT_TRAUMA_COLOANA_CERVICALA" or "CT_COLOANA_CERVICALA" or "IRM_CERVICAL" => new[] { "cervical", "vertebrelor cervicale", "c3-c4", "c4-c5", "c5-c6", "c6-c7", "atlanto-occipital", "atlanto-axial", "interapofizar", "odontoid", "fractura cervicala" },
            "URG_CT_TRAUMA_COMPLET" => new[] { "cranio-cerebral", "fractura", "cisternele", "vertebrelor", "costale", "pneumotorax", "hemotorax", "ficat", "splina", "rinichi", "bazin", "intraperitoneal" },
            "IRM_LOMBAR" or "CT_COLOANA_LOMBARA" => new[] { "lombar", "l1-l2", "l2-l3", "l3-l4", "l4-l5", "l5-s1", "con medular", "spondilodiscartroza", "pfirrmann", "modic", "intraspongioase", "interapofizara", "disco-radicular", "protruzie discala", "hernie de disc" },
            "IRM_TORACAL" or "CT_COLOANA_TORACALA" => new[] { "toracal", "dorsal", "cordon medular toracal", "canal spinal toracal", "t11", "t12", "t8", "t3-t10", "costo-vertebral" },
            "IRM_GENUNCHI" or "CT_GENUNCHI" => new[] { "genunchi", "menisc", "patel", "incrucisat", "colateral", "condromalacie", "baker", "gastrocnemio", "femuro-tibial", "suprapatelar", "insall-salvati", "wiberg", "gonartroza" },
            "CT_GLEZNA" => new[] { "glezn", "talus", "calcane", "tibio-tarsian", "maleol", "achilean", "peronier", "sindesmoza" },
            "CT_COT" => new[] { "cot", "olecran", "coronoid", "cupusoara", "radiocapitular", "humero-radial", "humero-ulnar" },
            "CT_GAT" => new[] { "gat", "gât", "faring", "laring", "parotid", "submandibular", "tiroid", "carotidian", "jugular", "cervical" },
            "CT_UMAR" => new[] { "umar", "umăr", "scapulo-humeral", "acromion", "acromio-clavicular", "glenoida", "cap humeral", "coafa rotatorilor", "bursa subacromio" },
            "CT_PUMN" => new[] { "pumn", "carp", "scafoid", "semilunar", "radio-carpian", "radio-ulnar", "metacarp", "piramidal", "trapezoid", "capitat" },
            "CT_BAZIN" => new[] { "bazin", "inel pelvin", "coxo-femural", "sacro-iliac", "iliac", "ischion", "pubis", "simfiza pubiana", "acetabul", "cap femural", "col femural" },
            "CT_COAPSA" => new[] { "coapsa", "coapsă", "femur", "femural", "diafiza femurala", "muscular coapsa", "fascia lata" },
            "CT_GAMBA" => new[] { "gamba", "gambă", "tibie", "peroneu", "fibula", "diafiza tibiala", "maleola", "tibio-fibular" },
            "CT_ANTEPICIOR" => new[] { "antepicior", "metatars", "tarso-metatarsian", "lisfranc", "falange", "antepiciorului", "cuneiform", "cuboid" },
            "IRM_HIPOFIZA" => new[] { "hipofiz", "selar", "chiasma", "tija pituitara", "adenohipofiz", "cavernos" },
            "IRM_PELVIS" => new[] { "uter", "ovar", "endometru", "miometru", "prostat", "vezica urinara", "douglas", "rect" },
            _ => new[] { "irm", "ct", "normal", "modificari" }
        };
    }

    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "fara", "fără", "nivel", "nivelul", "care", "este", "sunt", "acest", "acesta", "aceasta",
        "dupa", "după", "prin", "pentru", "asfel", "astfel", "facem", "protocol", "interpretare",
        "mai", "important", "importante", "foarte", "usor", "ușor", "discret", "discreta", "partea"
    };

    private static (string Last, string First) ParseDicomName(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return ("", "");
        if (raw.Contains('^'))
        {
            var parts = raw.Split('^');
            string last = parts[0].Trim();
            string first = parts.Length > 1 ? parts[1].Trim() : "";
            return (last, first);
        }
        var tokens = raw.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length >= 2)
        {
            return (tokens[0], string.Join(" ", tokens.Skip(1)));
        }
        return (raw.Trim(), "");
    }

    private static string FormatAge(string? rawAge)
    {
        if (string.IsNullOrWhiteSpace(rawAge)) return "ani";
        string clean = rawAge.Trim().TrimStart('0');
        if (clean.EndsWith("Y", StringComparison.OrdinalIgnoreCase))
            clean = clean[..^1] + " ani";
        else if (!clean.Contains("an", StringComparison.OrdinalIgnoreCase))
            clean += " ani";
        return clean;
    }

    private static string FormatDate(string? dcmDate)
    {
        if (string.IsNullOrWhiteSpace(dcmDate) || dcmDate.Trim() == "-")
            return DateTime.Now.ToString("dd.MM.yyyy");

        string trimmed = dcmDate.Trim();
        if (System.Text.RegularExpressions.Regex.IsMatch(trimmed, @"^\d{2}\.\d{2}\.\d{4}$"))
            return trimmed;

        string clean = new string(trimmed.Where(char.IsDigit).ToArray());
        if (clean.Length >= 8)
        {
            // YYYYMMDD
            string y = clean.Substring(0, 4);
            string m = clean.Substring(3, 2);
            string d = clean.Substring(5, 2);
            // Check if YYYY is first:
            if (int.TryParse(clean.Substring(0, 4), out int yr) && yr >= 1900 && yr <= 2100)
            {
                y = clean.Substring(0, 4);
                m = clean.Substring(4, 2);
                d = clean.Substring(6, 2);
                return $"{d}.{m}.{y}";
            }
        }

        if (DateTime.TryParse(trimmed, out var dt))
        {
            return dt.ToString("dd.MM.yyyy");
        }

        return trimmed;
    }
}
