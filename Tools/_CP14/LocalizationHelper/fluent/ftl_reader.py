def read_ftl(path: str) -> dict:
    """
        The function looks at each line of the ftl
        file and determines by the indentation in the line whether
        it is a new prototype or an attribute of an old one.
    """
    prototypes = {

    }

    last_prototype = ""

    try:
        with open(path, encoding="utf-8") as file:
            for line in file.readlines():
                if line.startswith("#") or line.startswith("\n"):
                    continue

                if not line.startswith("    "):
                    proto_id, proto_name = line.split(" = ")
                    proto_id = proto_id.replace("ent-", "")
                    last_prototype = proto_id
                    prototypes[proto_id] = {"name": proto_name.strip(),
                                            "desc": None,
                                            "suffix": None
                                            }
                else:
                    if "desc" in line:
                        attr = "desc"
                    elif "suffix" in line:
                        attr = "suffix"

                    prototypes[last_prototype][attr] = line.split(" = ")[-1].strip()
    except Exception as e:
        print(f"An error occurred while reading a file {path}, error - {e}")

    return prototypes