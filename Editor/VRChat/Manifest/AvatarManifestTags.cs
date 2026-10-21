using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Orbiters.Toolkit.Editor.VRChat.Manifest
{
    /// <summary>What a menu control is about (the manifest's tags, in their order).</summary>
    [Flags]
    public enum AvatarTags
    {
        None = 0,
        Clothing = 1 << 0, Accessory = 1 << 1, Hair = 1 << 2, Body = 1 << 3, Face = 1 << 4, Nsfw = 1 << 5, Pose = 1 << 6,
        Emote = 1 << 7, Locomotion = 1 << 8, Prop = 1 << 9, Effect = 1 << 10, System = 1 << 11, Orb = 1 << 12,
    }

    /// <summary>
    /// Tags from the words of a control's label and of its parameters' names, in English, French, Spanish, German and
    /// Japanese: hints for people and assistants, so only words that say it plainly count. Latin words match whole (accents,
    /// case and a plural s ignored; "Hoodie2" and "VF12_Clothes/HoodieOn" split into words); Japanese ones anywhere in the
    /// label. NSFW is kept to words that leave no doubt (nude, SPS…): a breast size slider is about the body.
    /// </summary>
    public static class AvatarManifestTags
    {
        // Several words joined by + are a phrase ("face+tracking").
        private static readonly (AvatarTags tag, string latin, string japanese)[] Words =
        {
            (AvatarTags.Clothing,
                "clothes clothing cloth outfit shirt tshirt t+shirt top hoodie sweater sweatshirt jumper jacket coat vest blazer cardigan pants trousers " +
                "jeans shorts skirt dress gown suit uniform kimono yukata leggings tights stockings sock shoe boot sneaker heels sandal glove mitten hat " +
                "cap beanie hood bra underwear panties lingerie swimsuit bikini apron overalls costume garment " +
                "vetement tenue haut sweat pull veste manteau gilet pantalon jupe robe chemise chaussure botte chaussette gant chapeau casquette capuche culotte maillot " +
                "ropa atuendo camisa camiseta sudadera chaqueta abrigo chaleco pantalones falda vestido traje zapatos zapatillas botas calcetines guantes sombrero gorra capucha sujetador bragas " +
                "kleidung kleider hemd oberteil pullover jacke mantel weste hose rock kleid anzug schuhe stiefel socken strumpfe handschuhe hut mutze kapuze unterwasche badeanzug",
                "服 衣装 衣服 シャツ パーカー ジャケット コート ズボン パンツ スカート ワンピース ドレス 靴 ブーツ ソックス 手袋 帽子 キャップ フード 下着 水着 制服 着物 浴衣 トップス ボトムス セーター ニット タイツ"),
            (AvatarTags.Accessory,
                "accessory accessories glasses sunglasses goggles earring piercing necklace pendant bracelet ring watch collar choker chain jewelry jewellery " +
                "jewel bag backpack purse wing halo crown tiara mask headphones headset harness belt tie bowtie ribbon hairclip hairpin scarf badge cape veil " +
                "accessoire lunettes boucle collier pendentif bague montre chaine bijou sac aile aureole couronne masque casque harnais ceinture cravate noeud ruban echarpe voile " +
                "accesorio accesorios gafas lentes anteojos pendientes aretes colgante pulsera anillo reloj cadena joyas bolso mochila alas aureola corona auriculares arnes cinturon corbata lazo cinta bufanda capa " +
                "zubehor brille sonnenbrille ohrringe halskette kette anhanger armband uhr halsband schmuck tasche rucksack flugel heiligenschein krone maske kopfhorer geschirr gurtel krawatte schleife schal umhang",
                "アクセ メガネ 眼鏡 めがね サングラス ゴーグル ピアス イヤリング ネックレス ペンダント ブレスレット 指輪 腕時計 首輪 チョーカー チェーン バッグ リュック 翼 羽根 天使の輪 王冠 ティアラ マスク ヘッドホン ヘッドフォン ハーネス ベルト ネクタイ リボン ヘアピン マフラー スカーフ マント"),
            (AvatarTags.Hair,
                "hair hairstyle haircut ponytail pigtails twintails braid bun bangs fringe curls mohawk afro wig " +
                "cheveux coiffure tresse chignon frange perruque meche " +
                "pelo cabello peinado coleta trenza flequillo peluca " +
                "haar haare frisur zopf zopfe pferdeschwanz dutt perucke",
                "髪 ヘア 前髪 ポニーテール ツインテール 三つ編み おさげ お団子 ウィッグ アホ毛"),
            (AvatarTags.Body,
                "body skin tail ear horn fur fluff paw claw teeth fang tongue muscle abs belly tummy chest breast boob butt thigh leg arm hand feet foot " +
                "height size weight fat thin chubby buff toe nail whiskers snout muzzle " +
                "corps peau queue oreille corne fourrure patte griffe dent croc langue ventre poitrine sein fesse cuisse jambe bras main pied taille poids ongle museau " +
                "cuerpo piel cola oreja cuerno pelaje pata garra diente colmillo lengua musculo barriga pecho seno trasero muslo pierna brazo mano pie altura tamano peso bigote hocico " +
                "korper haut schwanz ohr ohren horner fell pfote kralle zahne zunge muskel bauch brust bruste oberschenkel bein beine arme hande fusse fuße grosse große gewicht schnauze",
                "体 肌 尻尾 しっぽ シッポ 耳 ケモミミ 角 ツノ 胸 おっぱい バスト お尻 太もも 腕 脚 爪 牙 舌 筋肉 身長 体重 肉球"),
            (AvatarTags.Face,
                "face facial expression emotion eye pupil iris eyebrow brow eyelid lash mouth lip smile blush tear makeup lipstick eyeliner freckle cheek nose " +
                "viseme ft facetracking face+tracking eyetracking eye+tracking " +
                "visage emotion yeux oeil pupille sourcil cil bouche levre sourire rougir larme maquillage joue nez " +
                "cara rostro expresion emocion ojo pupila ceja pestana boca labio sonrisa rubor sonrojo lagrima maquillaje mejilla nariz " +
                "gesicht ausdruck augen auge pupille augenbraue wimper mund lippe lacheln erroten trane schminke wange nase",
                "顔 表情 目 瞳 眉 まつ毛 口 唇 笑顔 頬 赤面 照れ 涙 メイク 化粧 鼻 フェイストラッキング アイトラッキング"),
            (AvatarTags.Nsfw,
                "nsfw nude nudity naked lewd erp porn hentai r18 sps dps tps penetrator penetration orifice genital genitals penis dick cock vagina pussy anus " +
                "anal nipple areola cum sex sexual " +
                "nue nudite sexe vagin teton " +
                "desnudo desnuda desnudez sexo pene pezon " +
                "nackt nacktheit brustwarze brustwarzen",
                "裸 ヌード エロ ちんこ ちんちん まんこ 乳首 性器"),
            (AvatarTags.Pose,
                "pose posing sit sitting seated lie lying laying sleep sleeping stand standing crouch crouching prone kneel kneeling lean " +
                "assis assise asseoir allonge couche dormir debout accroupi " +
                "sentado sentada sentarse acostado tumbado de+pie agachado arrodillado " +
                "posen sitzen sitzend liegen liegend schlafen stehen stehend hocken knien",
                "ポーズ 座る 座り 寝る 寝転 寝そべり 立つ 立ち しゃがみ 正座"),
            (AvatarTags.Emote,
                "emote dance dancing wave waving clap clapping cheer laugh laughing cry crying point pointing salute hug kiss backflip faint shrug highfive high+five " +
                "danse danser saluer salut applaudir rire pleurer bisou calin " +
                "baile bailar saludar saludo aplaudir reir llorar beso abrazo " +
                "tanz tanze tanzen winken klatschen lachen weinen kuss umarmung",
                "エモート ダンス 踊り 踊る 手を振る 拍手 笑う 泣く キス ハグ"),
            (AvatarTags.Locomotion,
                "locomotion gogo gogoloco goloco go+loco fly flying flight jump jumping speed run running walk walking stationary noclip " +
                "voler sauter saut vitesse marcher courir " +
                "locomocion volar vuelo velocidad saltar salto caminar correr " +
                "fortbewegung fliegen flug geschwindigkeit springen sprung laufen gehen rennen",
                "移動 ロコモーション 飛行 飛ぶ ジャンプ 速度 歩く 走る"),
            (AvatarTags.Prop,
                "prop item object weapon sword gun pistol rifle staff wand phone smartphone camera cup mug drink bottle food snack pen pencil sign guitar " +
                "instrument umbrella balloon plush plushie toy book flashlight lamp lantern torch screen tablet laptop ball shield axe hammer knife spear skateboard broom " +
                "objet arme epee fusil baton baguette telephone appareil tasse boisson bouteille nourriture stylo crayon panneau guitare parapluie ballon peluche jouet livre lampe lanterne ecran tablette bouclier hache marteau couteau balai " +
                "objeto arma espada pistola rifle varita telefono camara taza bebida botella comida boligrafo lapiz cartel guitarra paraguas globo juguete libro linterna lampara pantalla tableta escudo hacha martillo cuchillo escoba " +
                "objekt gegenstand waffe schwert gewehr stab zauberstab handy kamera tasse getrank flasche essen stift schild gitarre regenschirm pluschtier spielzeug buch taschenlampe laterne bildschirm axt messer besen",
                "小物 アイテム 武器 剣 刀 銃 杖 スマホ 携帯 カメラ コップ マグ 飲み物 ボトル 食べ物 お菓子 ペン 看板 ギター 楽器 傘 風船 ぬいぐるみ おもちゃ ランタン 盾 斧 ハンマー ナイフ ほうき"),
            (AvatarTags.Effect,
                "effect fx vfx particle glow glowing emission emissive light aura sparkle sparkly fire flame smoke trail rainbow audiolink shader dissolve hologram neon " +
                "lightning electric magic bubble snow rain star heart " +
                "effet lueur brillance lumiere etincelle feu flamme fumee trainee arc+en+ciel hologramme magie bulle neige pluie etoile coeur " +
                "efecto particula brillo emision luz luces chispa fuego llama humo estela arcoiris holograma magia burbuja nieve lluvia estrella corazon corazones " +
                "effekt effekte partikel leuchten glanz licht lichter funken feuer flamme flammen rauch spur regenbogen hologramm magie blase blasen schnee regen sterne herz herzen",
                "エフェクト パーティクル 光 発光 ライト オーラ キラキラ 炎 煙 軌跡 虹 オーディオリンク ホログラム ネオン 魔法 泡 雪 雨 星 ハート"),
            (AvatarTags.System,
                "settings setting option options config configuration reset menu toggle+all lock unlock safety performance debug sync osc parameter params gesture " +
                "afk mirror quest compatibility " +
                "parametre reglage reinitialiser verrou verrouiller securite " +
                "ajuste ajustes configuracion opcion opciones restablecer reiniciar bloqueo bloquear seguridad " +
                "einstellung einstellungen optionen zurucksetzen sperre sperren sicherheit",
                "設定 オプション リセット メニュー ロック 解除"),
        };

        // Each word as typed and with a plural s or es: its whole-word forms. Phrases are kept apart.
        private static readonly Dictionary<string, AvatarTags> Single = new Dictionary<string, AvatarTags>(StringComparer.Ordinal);
        private static readonly List<(string[] words, AvatarTags tag)> Phrases = new List<(string[] words, AvatarTags tag)>();
        private static readonly List<(string text, AvatarTags tag)> Japanese = new List<(string text, AvatarTags tag)>();

        static AvatarManifestTags()
        {
            foreach (var (tag, latin, japanese) in Words)
            {
                foreach (string word in latin.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    if (word.Contains("+")) { Phrases.Add((word.Split('+'), tag)); continue; }
                    Single.TryGetValue(word, out var known);
                    Single[word] = known | tag;
                }
                foreach (string text in japanese.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)) Japanese.Add((text, tag));
            }
        }

        /// <summary>The tags of a control from its label and the names of the parameters it sets.</summary>
        public static AvatarTags Of(string label, IEnumerable<string> parameters)
        {
            string plain = AvatarManifest.Plain(label);
            var tags = OfWords(Split(Fold(plain)));
            // Japanese as typed (its voiced marks are no accents), full and half width alike.
            string japanese = plain.Normalize(NormalizationForm.FormKC);
            foreach (var (text, tag) in Japanese) if (japanese.IndexOf(text, StringComparison.Ordinal) >= 0) tags |= tag;
            foreach (string parameter in parameters ?? Enumerable.Empty<string>()) tags |= OfWords(Split(Fold(Spaced(parameter))));
            return tags;
        }

        /// <summary>The tags' names, in the manifest's order.</summary>
        public static IEnumerable<string> Names(AvatarTags tags)
        {
            foreach (AvatarTags tag in Enum.GetValues(typeof(AvatarTags)))
                if (tag != AvatarTags.None && (tags & tag) != 0) yield return tag.ToString().ToLowerInvariant();
        }

        private static AvatarTags OfWords(List<string> words)
        {
            var tags = AvatarTags.None;
            for (int i = 0; i < words.Count; i++)
            {
                // As typed, then without the digits around it ("hoodie2"), then without a plural s or es (cheveux keep
                // their own entry).
                string word = words[i], letters = word.Trim('0', '1', '2', '3', '4', '5', '6', '7', '8', '9');
                if (letters.Length == 0) letters = word;
                if (Single.TryGetValue(word, out var tag) || Single.TryGetValue(letters, out tag)) tags |= tag;
                else if (letters.Length > 3 && letters.EndsWith("s", StringComparison.Ordinal) && Single.TryGetValue(letters.Substring(0, letters.Length - 1), out tag)) tags |= tag;
                else if (letters.Length > 4 && letters.EndsWith("es", StringComparison.Ordinal) && Single.TryGetValue(letters.Substring(0, letters.Length - 2), out tag)) tags |= tag;
                foreach (var (phrase, phraseTag) in Phrases)
                    if (i + phrase.Length <= words.Count && phrase.Select((w, k) => words[i + k] == w).All(same => same)) tags |= phraseTag;
            }
            return tags;
        }

        // Lower case, accents gone (full width letters as plain ones).
        private static string Fold(string text)
        {
            var folded = new StringBuilder(text.Length);
            foreach (char c in text.Normalize(NormalizationForm.FormKD))
                if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) folded.Append(c);
            return folded.ToString().Normalize(NormalizationForm.FormKC).ToLowerInvariant();
        }

        // Words of letters and digits: "VF12_Clothes/Hoodie2" gives vf12, clothes, hoodie2.
        private static List<string> Split(string text)
        {
            var words = new List<string>();
            var word = new StringBuilder();
            foreach (char c in text)
            {
                if (char.IsLetterOrDigit(c)) { word.Append(c); continue; }
                if (word.Length > 0) { words.Add(word.ToString()); word.Clear(); }
            }
            if (word.Length > 0) words.Add(word.ToString());
            return words;
        }

        // A parameter name's words: camel case and digits split ("HoodieOn" gives "Hoodie On").
        private static string Spaced(string name)
        {
            var words = new StringBuilder(name.Length + 8);
            for (int i = 0; i < name.Length; i++)
            {
                char c = name[i];
                if (i > 0 && char.IsUpper(c) && (char.IsLower(name[i - 1]) || i + 1 < name.Length && char.IsLower(name[i + 1]) && char.IsUpper(name[i - 1]))) words.Append(' ');
                words.Append(c);
            }
            return words.ToString();
        }
    }
}
