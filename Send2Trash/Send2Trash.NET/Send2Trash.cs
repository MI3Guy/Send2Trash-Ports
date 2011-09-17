// Based on the python library send2trash.
// http://www.hardcoded.net/articles/send-files-to-trash-on-all-platforms.htm

// Uses the same BSD License.

// Copyright 2011 John Bentley

// Not sure if this applies due to this being a port. Included anyway.
// Below is the original notice.
// Copyright 2010 Hardcoded Software (http://www.hardcoded.net)

// This software is licensed under the "BSD" License as described in the "LICENSE" file, 
// which should be included with this package. The terms are also available at 
// http://www.hardcoded.net/licenses/bsd_license

// This is a reimplementation of plat_other.py with reference to the
// freedesktop.org trash specification:
//   [1] http://www.freedesktop.org/wiki/Specifications/trash-spec
//   [2] http://www.ramendik.ru/docs/trashspec.html
// See also:
//   [3] http://standards.freedesktop.org/basedir-spec/basedir-spec-latest.html
//
// For external volumes this implementation will raise an exception if it can't
// find or create the user's trash directory.
using System;
using System.IO;
using Mono.Unix;
using Mono.Unix.Native;

namespace Send2Trash {
	public static class Send2Trash {
		
		const string FILES_DIR = "files";
		const string INFO_DIR = "info";
		const string INFO_SUFFIX = ".trashinfo";
		static readonly uint UID = Syscall.getuid();
		static readonly string XDG_DATA_HOME = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
		static readonly string HOMETRASH = Path.Combine(XDG_DATA_HOME, "Trash");
		static readonly string TOPDIR_TRASH = ".Trash";
		static readonly string TOPDIR_FALLBACK = ".Trash-" + UID.ToString();
		
		
		static bool is_parent(string parent, string path) {
			path = UnixPath.GetFullPath(path);
			parent = UnixPath.GetRealPath(parent);
			return path.StartsWith(parent);
		}
		
		static string format_date(DateTime date) {
			return date.ToString("s");
		}
		
		static string info_for(string src, string top_dir) {
			if(top_dir == null || !is_parent(top_dir, src)) {
				src = UnixPath.GetFullPath(src);
			}
			else {
				src = new Uri("file://" + top_dir + "/").MakeRelativeUri(new Uri("file://" + src)).ToString();
			}
			
			return string.Format("[Trash Info]\nPath=\"{0}\"\nDeletionDate=\"{1}\"\n", src, format_date(DateTime.Now));
		}
		
/*def info_for(src, topdir):
    # ...it MUST not include a ".."" directory, and for files not "under" that
    # directory, absolute pathnames must be used. [2]
    if topdir is None or not is_parent(topdir, src):
        src = op.abspath(src)
    else:
        src = op.relpath(src, topdir)

    info  = "[Trash Info]\n"
    info += "Path=" + quote(src) + "\n"
    info += "DeletionDate=" + format_date(datetime.now()) + "\n"
    return info*/
		
		static void check_create(string dir) {
			if(!Directory.Exists(dir)) Syscall.mkdir(dir, FilePermissions.S_IRWXU);
		}
		
		static void trash_move(string src, string dst, string top_dir) {
			string filename = UnixPath.GetFileName(src);
			string filespath = UnixPath.Combine(dst, FILES_DIR);
			string infopath = UnixPath.Combine(dst, INFO_DIR);
			string base_name = Path.GetFileNameWithoutExtension(filename);
			string ext = Path.GetExtension(filename);
			
			int counter = 0;
			string destname = filename;
			while(File.Exists(UnixPath.Combine(filespath, destname)) || File.Exists(UnixPath.Combine(infopath, destname + INFO_SUFFIX)) ||
			      Directory.Exists(UnixPath.Combine(filespath, destname)) || Directory.Exists(UnixPath.Combine(infopath, destname + INFO_SUFFIX))) {
				++counter;
				destname = string.Format("{0} {1}{2}", base_name, counter, ext);
			}
			
			check_create(filespath);
			check_create(infopath);
			
			if(Directory.Exists(src)) {
				Directory.Move(src, UnixPath.Combine(filespath, destname));
			}
			else {
				File.Move(src, UnixPath.Combine(filespath, destname));
			}
			
			File.WriteAllText(UnixPath.Combine(infopath, destname + INFO_SUFFIX), info_for(src, top_dir));
		}
		
		static void trash_move(string src, string dst) {
			trash_move(src, dst, null);
		}
		
		static bool ismount(string path) {
			Stat s1, s2;
			try {
				if(Syscall.stat(path, out s1) != 0) return false;
				if(Syscall.stat(UnixPath.GetDirectoryName(path), out s2) != 0) return false;
			}
			catch {
				return false;
			}
			
			if(s1.st_dev != s2.st_dev) return true;
			if(s1.st_ino == s2.st_ino) return true;
			return false;
		}
		
		static string find_mount_point(string path) {
			path = UnixPath.GetRealPath(path);
			while(!ismount(path)) {
				path = UnixPath.GetDirectoryName(path);
				
			}
			return path;
		}
		
		
		static string find_ext_volume_global_trash(string volume_root) {
			string trash_dir = UnixPath.Combine(volume_root, TOPDIR_TRASH);
			if(!Directory.Exists(trash_dir)) return null;
			
			Stat s1;
			if(Syscall.lstat(trash_dir, out s1) != 0) throw new IOException();
			FilePermissions mode = s1.st_mode;
			
			if(UnixPath.GetRealPath(trash_dir) != UnixPath.GetFullPath(trash_dir) || (mode & FilePermissions.S_ISVTX) != FilePermissions.S_ISVTX) {
				return null;
			}
			
			trash_dir = UnixPath.Combine(trash_dir, UID.ToString());
			try {
				check_create(trash_dir);
			}
			catch { return null; }
			
			return trash_dir;
		}
		
		static string find_ext_volume_fallback_trash(string volume_root) {
			string trash_dir = UnixPath.Combine(volume_root, TOPDIR_FALLBACK);
			check_create(trash_dir);
			return trash_dir;
		}
		
		static string find_ext_volume_trash(string volume_root) {
			string trash_dir = find_ext_volume_global_trash(volume_root);
			if(trash_dir == null) trash_dir = find_ext_volume_fallback_trash(volume_root);
			return trash_dir;
		}
		
		static ulong get_dev(string path) {
			Stat buff;
			if(Syscall.lstat(path, out buff) != 0) throw new IOException();
			return buff.st_dev;
		}
		
		public static void Put(string path) {
			if(!File.Exists(path) && !Directory.Exists(path)) throw new FileNotFoundException("", path);
			if(Syscall.access(path, AccessModes.W_OK) != 0) throw new IOException();
			
			
			ulong path_dev = get_dev(path);
			ulong trash_dev = get_dev(Environment.GetFolderPath(Environment.SpecialFolder.Personal));
			
			string top_dir, dest_trash;
			
			if(path_dev == trash_dev) {
				top_dir = XDG_DATA_HOME;
				dest_trash = HOMETRASH;
			}
			else {
				top_dir = find_mount_point(path);
				trash_dev = get_dev(top_dir);
				if(trash_dev != path_dev) throw new IOException();
				dest_trash = find_ext_volume_trash(top_dir);
			}
			
			trash_move(path, dest_trash, top_dir);
		}
	}
}

/*

def info_for(src, topdir):
    # ...it MUST not include a ".."" directory, and for files not "under" that
    # directory, absolute pathnames must be used. [2]
    if topdir is None or not is_parent(topdir, src):
        src = op.abspath(src)
    else:
        src = op.relpath(src, topdir)

    info  = "[Trash Info]\n"
    info += "Path=" + quote(src) + "\n"
    info += "DeletionDate=" + format_date(datetime.now()) + "\n"
    return info

def check_create(dir):
    # use 0700 for paths [3]
    if not op.exists(dir):
        os.makedirs(dir, 0o700)

def trash_move(src, dst, topdir=None):
    filename = op.basename(src)
    filespath = op.join(dst, FILES_DIR)
    infopath = op.join(dst, INFO_DIR)
    base_name, ext = op.splitext(filename)

    counter = 0
    destname = filename
    while op.exists(op.join(filespath, destname)) or op.exists(op.join(infopath, destname + INFO_SUFFIX)):
        counter += 1
        destname = '%s %s%s' % (base_name, counter, ext)
    
    check_create(filespath)
    check_create(infopath)
    
    os.rename(src, op.join(filespath, destname))
    f = open(op.join(infopath, destname + INFO_SUFFIX), 'w')
    f.write(info_for(src, topdir))
    f.close()

def find_mount_point(path):
    # Even if something's wrong, "/" is a mount point, so the loop will exit.
    # Use realpath in case it's a symlink
    path = op.realpath(path) # Required to avoid infinite loop
    while not op.ismount(path):
        path = op.split(path)[0]
    return path

def find_ext_volume_global_trash(volume_root):
    # from [2] Trash directories (1) check for a .Trash dir with the right
    # permissions set.
    trash_dir = op.join(volume_root, TOPDIR_TRASH)
    if not op.exists(trash_dir):
        return None
    
    mode = os.lstat(trash_dir).st_mode
    # vol/.Trash must be a directory, cannot be a symlink, and must have the
    # sticky bit set.
    if not op.isdir(trash_dir) or op.islink(trash_dir) or not (mode & stat.S_ISVTX):
        return None

    trash_dir = op.join(trash_dir, str(uid))
    try:
        check_create(trash_dir)
    except OSError:
        return None
    return trash_dir

def find_ext_volume_fallback_trash(volume_root):
    # from [2] Trash directories (1) create a .Trash-$uid dir.
    trash_dir = op.join(volume_root, TOPDIR_FALLBACK)
    # Try to make the directory, if we can't the OSError exception will escape
    # be thrown out of send2trash.
    check_create(trash_dir)
    return trash_dir

def find_ext_volume_trash(volume_root):
    trash_dir = find_ext_volume_global_trash(volume_root)
    if trash_dir is None:
        trash_dir = find_ext_volume_fallback_trash(volume_root)
    return trash_dir

# Pull this out so it's easy to stub (to avoid stubbing lstat itself)
def get_dev(path):
    return os.lstat(path).st_dev

def send2trash(path):
    if not isinstance(path, str):
        path = str(path, sys.getfilesystemencoding())
    if not op.exists(path):
        raise OSError("File not found: %s" % path)
    # ...should check whether the user has the necessary permissions to delete
    # it, before starting the trashing operation itself. [2]
    if not os.access(path, os.W_OK):
        raise OSError("Permission denied: %s" % path)
    # if the file to be trashed is on the same device as HOMETRASH we
    # want to move it there.
    path_dev = get_dev(path)
    
    # If XDG_DATA_HOME or HOMETRASH do not yet exist we need to stat the
    # home directory, and these paths will be created further on if needed.
    trash_dev = get_dev(op.expanduser('~'))

    if path_dev == trash_dev:
        topdir = XDG_DATA_HOME
        dest_trash = HOMETRASH
    else:
        topdir = find_mount_point(path)
        trash_dev = get_dev(topdir)
        if trash_dev != path_dev:
            raise OSError("Couldn't find mount point for %s" % path)
        dest_trash = find_ext_volume_trash(topdir)
    trash_move(path, dest_trash, topdir)
*/